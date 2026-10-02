using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using CustomJSONData.CustomBeatmap;
using Heck;
using Heck.PlayView;
using JetBrains.Annotations;
using SiraUtil.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Vivify.Managers;
using Zenject;
using static Vivify.VivifyController;
#if !PRE_V1_37_1
using CustomJSONData;
#endif

// ReSharper disable FieldCanBeMadeReadOnly.Local
namespace Vivify.Controllers;

[PlayViewControllerSettings(100, "vivify")]
internal class AssetBundleDownloadViewController : BSMLResourceViewController, IPlayViewController
{
    [UIComponent("loadingbar")]
    private VerticalLayoutGroup _barGroup = null!;

    private Config _config = null!;
    private AssetDownloader _assetDownloader = null!;
    private View _currentView = View.None;

    private bool _doAbort;
    private uint _downloadChecksum;
    private bool _downloadFinished;

    [UIComponent("downloading")]
    private VerticalLayoutGroup _downloadingGroup = null!;

    private string? _downloadPath;
    private float _downloadProgress;
    private Coroutine? _downloadWaiter;
    private CancellationTokenSource? _downloadCancellation;
    private int _downloadRevision;
    private bool _downloadStarted;
    private bool _retired;
    private Task? _pendingPreparation;
    private Coroutine? _downloadCoroutine;
    private WebRequestScope? _apiScope;
    private WebRequestScope? _bundleScope;

    [UIComponent("error")]
    private VerticalLayoutGroup _error = null!;

    [UIComponent("errortext")]
    private TMP_Text _errorText = null!;

    private string _lastError = string.Empty;

    private Image _loadingBar = null!;
    private SiraLog _log = null!;
    private View _newView = View.None;

    [UIComponent("percentage")]
    private TMP_Text _percentageText = null!;

    [UIComponent("tos")]
    private VerticalLayoutGroup _tosGroup = null!;

    public event Action? Finished;

    private enum View
    {
        None,
        Tos,
        Downloading,
        Error
    }

    public override string ResourceName => "Vivify.Resources.AssetBundleDownloading.bsml";

    public bool Init(StartStandardLevelParameters standardLevelParameters)
    {
        RetireDownload();
        _downloadPath = null;
        _downloadFinished = false;
        _downloadStarted = false;
#if !PRE_V1_37_1
        if (standardLevelParameters.BeatmapLevel.previewMediaData is not FileSystemPreviewMediaData fileSystemPreviewMediaData)
        {
            return false;
        }

        CustomData beatmapCustomData =
            standardLevelParameters.BeatmapLevel.GetBeatmapCustomData(standardLevelParameters.BeatmapKey);
        CustomData levelCustomData = standardLevelParameters.BeatmapLevel.GetLevelCustomData();
#else
        if (standardLevelParameters.DifficultyBeatmap is not CustomDifficultyBeatmap customDifficultyBeatmap)
        {
            return false;
        }

        Version3CustomBeatmapSaveData saveData = (Version3CustomBeatmapSaveData)customDifficultyBeatmap.beatmapSaveData;
        CustomData beatmapCustomData = saveData.beatmapCustomData;
        CustomData levelCustomData = saveData.levelCustomData;
#endif

        // check is vivify map
        string[] requirements = beatmapCustomData.Get<List<object>>("_requirements")?.Cast<string>().ToArray() ?? [];
        if (!requirements.Contains(CAPABILITY))
        {
            return false;
        }

        // check if bundle already downloaded
#if !PRE_V1_37_1
        string path =
            Path.Combine(
                Path.GetDirectoryName(fileSystemPreviewMediaData._previewAudioClipPath)!,
                BUNDLE_FILE);
#else
        string path = Path.Combine(
            ((CustomBeatmapLevel)customDifficultyBeatmap.level).customLevelPath,
            BUNDLE_FILE);
#endif
        if (File.Exists(path))
        {
            return false;
        }

        uint? assetBundleChecksum =
            levelCustomData.Get<CustomData>(ASSET_BUNDLE)?.Get<uint?>(BUNDLE_CHECKSUM);

        if (assetBundleChecksum == null)
        {
            _lastError =
                $"This map is missing required assets for your game version.\n" +
                $"Please contact the mapper to update their map to include the assets.";
            _newView = View.Error;
        }
        else
        {
            _log.Error($"[{path}] not found, attempting to download remotely");
            uint checksum = assetBundleChecksum.Value;
            _doAbort = false;
            _downloadFinished = false;
            _downloadCancellation = new CancellationTokenSource();
            if (_config.AllowDownload)
            {
                StartDownload(path, checksum);
            }
            else
            {
                _downloadPath = path;
                _downloadChecksum = checksum;
            }
        }

        return true;
    }

    [UsedImplicitly]
    [Inject]
    private void Construct(SiraLog log, Config config, AssetDownloader assetDownloader)
    {
        _log = log;
        _config = config;
        _assetDownloader = assetDownloader;
        _newView = config.AllowDownload ? View.Downloading : View.Tos;
    }

    // TODO: figure out a way to resolve the fact that multiplayer does NOT have enough time to download bundles
    private IEnumerator DownloadAndSave(
        string savePath,
        uint checksum,
        int revision,
        CancellationToken cancellationToken)
    {
        _newView = View.Downloading;
        string url = _config.BundleRepository + checksum;
        _log.Debug($"Fetching asset bundle info from [{url}]");
        using WebRequestScope apiScope = new(UnityWebRequest.Get(url));
        _apiScope = apiScope;
        UnityWebRequest apiRequest = apiScope.Request;
        apiRequest.SendWebRequest();

        while (!apiRequest.isDone)
        {
            if (IsCurrentDownload(revision, cancellationToken))
            {
                yield return null;
                continue;
            }

            apiRequest.Abort();
            _log.Debug("Fetch cancelled");
            yield break;
        }

        if (!IsCurrentDownload(revision, cancellationToken))
        {
            yield break;
        }

#pragma warning disable CS0618
        if (apiRequest.isNetworkError || apiRequest.isHttpError)
        {
            if (apiRequest.isNetworkError)
            {
                _lastError = $"Network error while fetching bundle.\n{apiRequest.error}";
            }
            else if (apiRequest.isHttpError)
            {
                _lastError = $"Server sent error response code while fetching bundle.\n({apiRequest.responseCode})";
            }

            _log.Error(_lastError);
            _newView = View.Error;
            yield break;
        }
#pragma warning restore CS0618

        Task<BundlePreparationWorker.Result<string>> parse =
            BundlePreparationWorker.ParseDownloadUrl(apiRequest.downloadHandler.text, cancellationToken);
        _pendingPreparation = parse;
        while (!parse.IsCompleted)
        {
            if (!IsCurrentDownload(revision, cancellationToken))
            {
                yield break;
            }

            yield return null;
        }

        if (!IsCurrentDownload(revision, cancellationToken))
        {
            yield break;
        }

        BundlePreparationWorker.Result<string> parsed = parse.GetAwaiter().GetResult();
        _pendingPreparation = null;
        if (parsed.Error != null)
        {
            ExceptionDispatchInfo.Capture(parsed.Error).Throw();
        }

        string downloadUrl = parsed.Value;
        _log.Debug($"Attempting to download asset bundle from [{downloadUrl}]");
        using WebRequestScope bundleScope = new(UnityWebRequest.Get(downloadUrl));
        _bundleScope = bundleScope;
        UnityWebRequest www = bundleScope.Request;
        www.SendWebRequest();
        while (!www.isDone)
        {
            if (IsCurrentDownload(revision, cancellationToken))
            {
                _downloadProgress = www.downloadProgress;
                yield return null;
                continue;
            }

            www.Abort();
            _log.Debug("Download cancelled");
            yield break;
        }

        if (!IsCurrentDownload(revision, cancellationToken))
        {
            yield break;
        }

#pragma warning disable CS0618
        if (www.isNetworkError || www.isHttpError)
        {
            if (www.isNetworkError)
            {
                _lastError = $"Network error while downloading bundle.\n{www.error}";
            }
            else if (www.isHttpError)
            {
                _lastError = $"Server sent error response code while downloading bundle.\n({www.responseCode})";
            }

            _log.Error(_lastError);
            _newView = View.Error;
            yield break;
        }
#pragma warning restore CS0618

        _downloadProgress = 1;
        Task<BundlePreparationWorker.Result<bool>> write =
            BundlePreparationWorker.Save(savePath, www.downloadHandler.data, cancellationToken);
        _pendingPreparation = write;
        while (!write.IsCompleted)
        {
            if (!IsCurrentDownload(revision, cancellationToken))
            {
                yield break;
            }

            yield return null;
        }

        if (!IsCurrentDownload(revision, cancellationToken))
        {
            yield break;
        }

        BundlePreparationWorker.Result<bool> written = write.GetAwaiter().GetResult();
        _pendingPreparation = null;
        if (written.Error != null)
        {
            ExceptionDispatchInfo.Capture(written.Error).Throw();
        }

        _log.Debug($"Successfully downloaded bundle to [{savePath}]");
        _downloadFinished = true;
    }

    [UsedImplicitly]
    [UIAction("accept-click")]
    private void OnAcceptClick()
    {
        _config.AllowDownload = true;
        if (_downloadPath != null)
        {
            StartDownload(_downloadPath, _downloadChecksum);
        }
    }

    [UsedImplicitly]
    private void OnEarlyDismiss()
    {
        RetireDownload();
    }

    [UsedImplicitly]
    private void OnShow()
    {
        if (!_downloadFinished)
        {
            if (_downloadWaiter != null)
            {
                _assetDownloader.StopCoroutine(_downloadWaiter);
            }

            _downloadWaiter = _assetDownloader.StartCoroutine(WaitForDownload(_downloadRevision));
        }
        else if (!_retired && !_doAbort)
        {
            Finished?.Invoke();
        }
    }

    private void Start()
    {
        Vector2 loadingBarSize = new(0, 8);

        // shamelessly stolen from songcore
        _loadingBar = new GameObject("Loading Bar").AddComponent<Image>();
        RectTransform barTransform = (RectTransform)_loadingBar.transform;
        barTransform.SetParent(_barGroup.transform, false);
        barTransform.sizeDelta = loadingBarSize;
        Texture2D? tex = Texture2D.whiteTexture;
        Sprite? sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), Vector2.one * 0.5f, 100, 1);
        _loadingBar.sprite = sprite;
        _loadingBar.type = Image.Type.Filled;
        _loadingBar.fillMethod = Image.FillMethod.Horizontal;
        _loadingBar.color = new Color(1, 1, 1, 0.5f);

        Image loadingBackg = new GameObject("Background").AddComponent<Image>();
        RectTransform loadingBackTransform = (RectTransform)loadingBackg.transform;
        loadingBackTransform.sizeDelta = loadingBarSize;
        loadingBackTransform.SetParent(_barGroup.transform, false);
        loadingBackg.color = new Color(0, 0, 0, 0.2f);
    }

    private void Update()
    {
        if (_pendingPreparation?.IsCompleted == true)
        {
            _pendingPreparation = null;
        }

        if (_currentView != _newView)
        {
            _currentView = _newView;
            switch (_currentView)
            {
                case View.Tos:
                    _tosGroup.gameObject.SetActive(true);
                    _downloadingGroup.gameObject.SetActive(false);
                    _error.gameObject.SetActive(false);
                    break;

                case View.Downloading:
                    _tosGroup.gameObject.SetActive(false);
                    _downloadingGroup.gameObject.SetActive(true);
                    _error.gameObject.SetActive(false);
                    break;

                case View.Error:
                    _tosGroup.gameObject.SetActive(false);
                    _downloadingGroup.gameObject.SetActive(false);
                    _error.gameObject.SetActive(true);
                    _errorText.text = _lastError;
                    break;
            }
        }

        if (_currentView != View.Downloading)
        {
            return;
        }

        _loadingBar.fillAmount = _downloadProgress;
        float percentage = _downloadProgress * 100;
        _percentageText.text = $"{percentage:0.0}%";
    }

    protected override void OnDestroy()
    {
        _retired = true;
        try
        {
            RetireDownload();
        }
        finally
        {
            base.OnDestroy();
        }
    }

    private bool IsCurrentDownload(int revision, CancellationToken cancellationToken)
    {
        return !_retired && !_doAbort && revision == _downloadRevision && !cancellationToken.IsCancellationRequested;
    }

    private void StartDownload(string path, uint checksum)
    {
        if (_downloadStarted || _retired || _downloadCancellation == null)
        {
            return;
        }

        _downloadStarted = true;
        _downloadCoroutine =
            _assetDownloader.StartCoroutine(DownloadAndSave(path, checksum, _downloadRevision, _downloadCancellation.Token));
    }

    private void RetireDownload()
    {
        _doAbort = true;
        _downloadRevision++;
        _downloadCancellation?.Cancel();
        _downloadCancellation?.Dispose();
        _downloadCancellation = null;
        if (_downloadCoroutine != null && _assetDownloader != null)
        {
            _assetDownloader.StopCoroutine(_downloadCoroutine);
        }

        _downloadCoroutine = null;
        if (_downloadWaiter != null && _assetDownloader != null)
        {
            _assetDownloader.StopCoroutine(_downloadWaiter);
        }

        _downloadWaiter = null;
        _apiScope?.Dispose();
        _apiScope = null;
        _bundleScope?.Dispose();
        _bundleScope = null;
    }

    private IEnumerator WaitForDownload(int revision)
    {
        while (!_downloadFinished)
        {
            if (_retired || _doAbort || revision != _downloadRevision)
            {
                yield break;
            }

            yield return null;
        }

        if (!_retired && !_doAbort && revision == _downloadRevision)
        {
            Finished?.Invoke();
        }
    }

    internal class AssetDownloader : MonoBehaviour;

    private sealed class WebRequestScope : IDisposable
    {
        private bool _disposed;

        internal WebRequestScope(UnityWebRequest request)
        {
            Request = request;
        }

        internal UnityWebRequest Request { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                Request.Abort();
            }
            finally
            {
                Request.Dispose();
            }
        }
    }
}
