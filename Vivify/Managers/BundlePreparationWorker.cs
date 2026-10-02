using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Vivify.Managers;

internal static class BundlePreparationWorker
{
    private static readonly object _gate = new();
    private static Task _tail = Task.CompletedTask;

    internal static Task<Result<string>> ParseDownloadUrl(string json, CancellationToken cancellationToken)
    {
        return Enqueue(new JsonRequest(json, cancellationToken).Run);
    }

    internal static Task<Result<bool>> Save(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        return Enqueue(new SaveRequest(path, bytes, cancellationToken).Run);
    }

    private static Task<Result<T>> Enqueue<T>(Func<Result<T>> prepare)
    {
        lock (_gate)
        {
            Task<Result<T>> task = _tail.ContinueWith(
                static (_, state) => ((Func<Result<T>>)state!).Invoke(),
                prepare,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            _tail = task;
            _ = task.ContinueWith(
                static completed =>
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_tail, completed))
                        {
                            _tail = Task.CompletedTask;
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            return task;
        }
    }

    internal sealed class Result<T>
    {
        internal Result(T value)
        {
            Value = value;
        }

        internal Result(Exception error)
        {
            Error = error;
        }

        internal T Value { get; } = default!;

        internal Exception? Error { get; }
    }

    private sealed class JsonRequest
    {
        private readonly CancellationToken _cancellationToken;
        private readonly string _json;

        internal JsonRequest(string json, CancellationToken cancellationToken)
        {
            _json = json;
            _cancellationToken = cancellationToken;
        }

        internal Result<string> Run()
        {
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                RepoJson json = JsonUtility.FromJson<RepoJson>(_json);
                string downloadUrl = json.downloadUrl;
                _cancellationToken.ThrowIfCancellationRequested();
                return new Result<string>(downloadUrl);
            }
            catch (Exception error)
            {
                return new Result<string>(error);
            }
        }
    }

    private sealed class SaveRequest
    {
        private readonly CancellationToken _cancellationToken;
        private readonly string _path;
        private byte[]? _bytes;

        internal SaveRequest(string path, byte[] bytes, CancellationToken cancellationToken)
        {
            _path = path;
            _bytes = bytes;
            _cancellationToken = cancellationToken;
        }

        internal Result<bool> Run()
        {
            string? temporaryPath = null;
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporaryPath, _bytes!);
                _cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(_path))
                {
                    File.Replace(temporaryPath, _path, null);
                }
                else
                {
                    File.Move(temporaryPath, _path);
                }

                temporaryPath = null;
                return new Result<bool>(true);
            }
            catch (Exception error)
            {
                return new Result<bool>(error);
            }
            finally
            {
                _bytes = null;
                if (temporaryPath != null)
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
    }

    [Serializable]
    private sealed class RepoJson
    {
#pragma warning disable SA1401
#pragma warning disable SA1307
#pragma warning disable CS8618
        public string downloadUrl;
#pragma warning restore CS8618
#pragma warning restore SA1307
#pragma warning restore SA1401
    }
}
