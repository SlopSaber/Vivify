using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Zenject;
using Object = UnityEngine.Object;

namespace Vivify.Managers;

internal class DepthShaderManager : IInitializable, IDisposable
{
    private const string PATH = "Vivify.Resources.DepthBlit";

    private Task? _loading;
    private bool _disposed;

    internal Material? DepthArrayMaterial { get; private set; }

    internal Material? DepthMaterial { get; private set; }

    public void Initialize()
    {
        if (_disposed || _loading != null)
        {
            return;
        }

        if (SynchronizationContext.Current == null)
        {
            throw new InvalidOperationException("Depth shaders must be initialized on the Unity owner context.");
        }

        _loading = Load();
    }

    public void Dispose()
    {
        _disposed = true;
        if (DepthMaterial != null)
        {
            Object.Destroy(DepthMaterial);
            DepthMaterial = null;
        }

        if (DepthArrayMaterial != null)
        {
            Object.Destroy(DepthArrayMaterial);
            DepthArrayMaterial = null;
        }
    }

    // shamelessly stolen from AssetBundleLoadingTools
    private static async Task<AssetBundle?> LoadFromMemoryAsync(byte[] binary, uint crc)
    {
        TaskCompletionSource<AssetBundle> taskCompletionSource = new();
        AssetBundleCreateRequest? bundleRequest = AssetBundle.LoadFromMemoryAsync(binary, crc);
        bundleRequest.completed += _ =>
        {
            taskCompletionSource.SetResult(bundleRequest.assetBundle);
        };

        return await taskCompletionSource.Task;
    }

    private static async Task<T?> LoadAssetAsync<T>(AssetBundle assetBundle, string path)
        where T : Object
    {
        TaskCompletionSource<T> taskCompletionSource = new();
        AssetBundleRequest? assetRequest = assetBundle.LoadAssetAsync<T>(path);
        assetRequest.completed += _ =>
        {
            taskCompletionSource.SetResult((T)assetRequest.asset);
        };

        return await taskCompletionSource.Task;
    }

    private static byte[] ReadResourceBytes()
    {
        using Stream stream = typeof(DepthShaderManager).Assembly.GetManifestResourceStream(PATH)!;
        using MemoryStream memoryStream = new();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    private async Task Load()
    {
        AssetBundle? bundle = null;
        bool published = false;
        try
        {
            byte[] bytes = await Task.Run(ReadResourceBytes);
            if (_disposed)
            {
                return;
            }

#if V1_29_1
            const uint crc = 1355036397;
#else
            const uint crc = 1746663828;
#endif
            bundle = await LoadFromMemoryAsync(bytes, crc);
            if (bundle == null || _disposed)
            {
                return;
            }

            Task<Material?> depth = LoadAssetAsync<Material>(bundle, "assets/depthblit.mat");
            Task<Material?> array = LoadAssetAsync<Material>(bundle, "assets/depthblitarrayslice.mat");
            await Task.WhenAll(depth, array);
            if (_disposed)
            {
                return;
            }

            DepthMaterial = depth.GetAwaiter().GetResult();
            DepthArrayMaterial = array.GetAwaiter().GetResult();
            published = true;
        }
        catch (Exception error)
        {
            Plugin.Log.Error(error);
        }
        finally
        {
            if (bundle != null)
            {
                try
                {
#if LATEST
                    await bundle.UnloadAsync(!published);
#elif V1_29_1
                    bundle.Unload(!published);
#else
                    bundle.UnloadAsync(!published);
#endif
                }
                catch (Exception error)
                {
                    Plugin.Log.Error(error);
                }
            }
        }
    }

}
