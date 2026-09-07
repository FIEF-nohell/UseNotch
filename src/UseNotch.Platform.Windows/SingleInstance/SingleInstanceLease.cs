using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace UseNotch.Platform.Windows.SingleInstance;

public sealed class SingleInstanceLease : IDisposable
{
    private const int ActivationTimeoutMilliseconds = 750;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _listenerTask;
    private readonly string _pipeName;
    private bool _disposed;

    private SingleInstanceLease(Mutex mutex, string pipeName, Action onActivate, Action onQuit)
    {
        _mutex = mutex;
        _pipeName = pipeName;
        _listenerTask = ListenAsync(onActivate, onQuit, _cancellation.Token);
    }

    public static SingleInstanceLease? AcquireOrSignalExisting(
        Action onActivate,
        Action onQuit,
        bool requestQuit = false)
    {
        ArgumentNullException.ThrowIfNull(onActivate);
        ArgumentNullException.ThrowIfNull(onQuit);

        var identity = SingleInstanceIdentity.ForCurrentUserSession();
        var mutex = new Mutex(initiallyOwned: true, identity.MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            SignalExistingInstance(identity.PipeName, requestQuit ? (byte)2 : (byte)1);
            return null;
        }

        return new SingleInstanceLease(mutex, identity.PipeName, onActivate, onQuit);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        try
        {
            _listenerTask.Wait(ActivationTimeoutMilliseconds);
        }
        catch (AggregateException exception) when (exception.InnerException is OperationCanceledException)
        {
        }
        finally
        {
            _cancellation.Dispose();
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    private static void SignalExistingInstance(string pipeName, byte request)
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            client.Connect(ActivationTimeoutMilliseconds);
            client.WriteByte(request);
            client.Flush();
        }
        catch (IOException)
        {
            // The owner can be shutting down between mutex acquisition and the signal.
            // It already owns the scoped mutex, so starting a duplicate is still unsafe.
        }
        catch (TimeoutException)
        {
            // Treat an unresponsive owner as authoritative until it releases the mutex.
        }
    }

    private async Task ListenAsync(Action onActivate, Action onQuit, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                switch (server.ReadByte())
                {
                    case 1:
                        onActivate();
                        break;
                    case 2:
                        onQuit();
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                // A broken activation client should not end the owner process.
            }
        }
    }
}

public readonly record struct SingleInstanceIdentity(string MutexName, string PipeName)
{
    public static SingleInstanceIdentity ForCurrentUserSession()
    {
        var userSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        return Create(userSid, Process.GetCurrentProcess().SessionId);
    }

    public static SingleInstanceIdentity Create(string userSid, int sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        ArgumentOutOfRangeException.ThrowIfNegative(sessionId);

        var scope = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"UseNotch|{userSid}|{sessionId}")))[..24];
        return new SingleInstanceIdentity(
            $"Local\\UseNotch-{scope}",
            $"UseNotch-{scope}");
    }
}
