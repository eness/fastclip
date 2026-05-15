using System.Runtime.InteropServices;
using FastClip.Interop;
using FastClip.Models;

namespace FastClip.Services;

internal sealed class ExplorerContextResolver : IExplorerContextResolver
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(2);

    public Task<ExplorerContext> GetContextAsync(CancellationToken cancellationToken)
    {
        return GetContextWithTimeoutAsync(cancellationToken);
    }

    private async Task<ExplorerContext> GetContextWithTimeoutAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RunStaAsync(GetContextCore, cancellationToken)
                .WaitAsync(ResolveTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return ExplorerContext.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ExplorerContext.Empty;
        }
    }

    private static ExplorerContext GetContextCore()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            return ExplorerContext.Empty;
        }

        object? shell = null;
        object? windows = null;
        ExplorerContext? activeContext = null;
        ExplorerContext? fallbackContext = null;

        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return ExplorerContext.Empty;
            }

            dynamic shellDynamic = shell;
            windows = shellDynamic.Windows();
            var foregroundWindow = NativeMethods.GetForegroundWindow();

            foreach (var window in (System.Collections.IEnumerable)windows)
            {
                try
                {
                    var context = TryGetWindowContext(window, foregroundWindow, out var isForegroundExplorer);
                    if (context is null || context.IsEmpty)
                    {
                        continue;
                    }

                    if (isForegroundExplorer)
                    {
                        activeContext = context;
                        return activeContext;
                    }

                    fallbackContext ??= context;
                }
                finally
                {
                    ReleaseComObject(window);
                }
            }
        }
        catch
        {
            return ExplorerContext.Empty;
        }
        finally
        {
            ReleaseComObject(windows);
            ReleaseComObject(shell);
        }

        return activeContext ?? fallbackContext ?? ExplorerContext.Empty;
    }

    private static ExplorerContext? TryGetWindowContext(object window, IntPtr foregroundWindow, out bool isForegroundExplorer)
    {
        isForegroundExplorer = false;

        try
        {
            dynamic explorerWindow = window;
            string executable = Path.GetFileName(((string)explorerWindow.FullName) ?? string.Empty);
            if (!executable.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var hwnd = new IntPtr(Convert.ToInt64(explorerWindow.HWND));
            isForegroundExplorer = hwnd == foregroundWindow;

            var folderPath = GetFolderPath(explorerWindow);
            var selectedFilePath = GetSelectedFilePath(explorerWindow);
            return new ExplorerContext(selectedFilePath, folderPath);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetFolderPath(dynamic window)
    {
        object? document = null;
        object? folder = null;
        object? self = null;

        try
        {
            document = window.Document;
            folder = document?.GetType().InvokeMember("Folder", System.Reflection.BindingFlags.GetProperty, null, document, null);
            self = folder?.GetType().InvokeMember("Self", System.Reflection.BindingFlags.GetProperty, null, folder, null);
            var path = self?.GetType().InvokeMember("Path", System.Reflection.BindingFlags.GetProperty, null, self, null) as string;
            return string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) ? null : path;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObject(self);
            ReleaseComObject(folder);
            ReleaseComObject(document);
        }
    }

    private static string? GetSelectedFilePath(dynamic window)
    {
        object? document = null;
        object? selectedItems = null;
        object? item = null;

        try
        {
            document = window.Document;
            selectedItems = document?.GetType().InvokeMember("SelectedItems", System.Reflection.BindingFlags.InvokeMethod, null, document, null);
            if (selectedItems is null)
            {
                return null;
            }

            var count = Convert.ToInt32(selectedItems.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, selectedItems, null));
            if (count != 1)
            {
                return null;
            }

            item = selectedItems.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, selectedItems, [0]);
            var path = item?.GetType().InvokeMember("Path", System.Reflection.BindingFlags.GetProperty, null, item, null) as string;
            return string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : path;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObject(item);
            ReleaseComObject(selectedItems);
            ReleaseComObject(document);
        }
    }

    private static void ReleaseComObject(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.FinalReleaseComObject(instance);
        }
    }

    private static Task<T> RunStaAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                tcs.TrySetResult(action());
            }
            catch (OperationCanceledException ex)
            {
                tcs.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}
