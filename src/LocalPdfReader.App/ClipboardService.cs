using System.Runtime.InteropServices;

namespace LocalPdfReader.App;

public interface IClipboardService
{
    Task SetTextAsync(string text, CancellationToken cancellationToken);
}

public sealed class WpfClipboardService : IClipboardService
{
    private const int ClipboardCannotOpenHResult = unchecked((int)0x800401D0);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(300)
    ];

    private readonly Action<string> _writeText;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public WpfClipboardService()
        : this(
            text => System.Windows.Clipboard.SetDataObject(text, copy: true),
            Task.Delay)
    {
    }

    public WpfClipboardService(
        Action<string> writeText,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _writeText = writeText ?? throw new ArgumentNullException(nameof(writeText));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    public async Task SetTextAsync(string text, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _writeText(text);
                return;
            }
            catch (COMException exception) when (
                exception.HResult == ClipboardCannotOpenHResult
                && attempt < RetryDelays.Length)
            {
                await _delay(RetryDelays[attempt], cancellationToken);
            }
        }
    }
}
