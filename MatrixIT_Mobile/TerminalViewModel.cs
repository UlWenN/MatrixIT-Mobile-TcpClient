using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace MatrixIT_Mobile;

public sealed partial class TerminalViewModel :
    INotifyPropertyChanged,
    IDisposable
{
    private static readonly TimeSpan ConnectionTimeout =
        TimeSpan.FromSeconds(10);

    private readonly IDatabaseService _databaseService;

    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private readonly Command _connectCommand;
    private readonly Command _disconnectCommand;
    private readonly Command _sendCommand;
    private readonly Command _clearHistoryCommand;

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _connectionCancellation;

    private bool _disposed;

    private string _ipAddress = string.Empty;

    public string IpAddress
    {
        get => _ipAddress;
        set
        {
            if (SetProperty(ref _ipAddress, value))
            {
                ValidationMessage = string.Empty;
            }
        }
    }

    private string _port = string.Empty;

    public string Port
    {
        get => _port;
        set
        {
            if (SetProperty(ref _port, value))
            {
                ValidationMessage = string.Empty;
            }
        }
    }

    private string _messageText = string.Empty;

    public string MessageText
    {
        get => _messageText;
        set
        {
            if (SetProperty(ref _messageText, value))
            {
                RefreshCommandStates();
            }
        }
    }

    private string _validationMessage = string.Empty;

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationMessage));
            }
        }
    }

    public bool HasValidationMessage =>
        !string.IsNullOrWhiteSpace(ValidationMessage);

    private string _statusText = "Нет подключения";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private Color _statusColor = Colors.Gray;

    public Color StatusColor
    {
        get => _statusColor;
        private set => SetProperty(ref _statusColor, value);
    }

    private bool _isConnected;

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(CanEditEndpoint));
                RefreshCommandStates();
            }
        }
    }

    private bool _isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanEditEndpoint));
                RefreshCommandStates();
            }
        }
    }

    private bool _isSending;

    public bool IsSending
    {
        get => _isSending;
        private set
        {
            if (SetProperty(ref _isSending, value))
            {
                RefreshCommandStates();
            }
        }
    }

    public bool CanEditEndpoint =>
        !IsConnected && !IsBusy;

    public ObservableCollection<LogMessage> Messages { get; } = new();

    public ICommand ConnectCommand => _connectCommand;

    public ICommand DisconnectCommand => _disconnectCommand;

    public ICommand SendCommand => _sendCommand;

    public ICommand ClearHistoryCommand => _clearHistoryCommand;

    public Task Initialization { get; }

    public TerminalViewModel()
        : this(new DatabaseService())
    {
    }

    public TerminalViewModel(
        IDatabaseService databaseService)
    {
        _databaseService = databaseService
            ?? throw new ArgumentNullException(
                nameof(databaseService));

        _connectCommand = new Command(
            async () => await ConnectAsync(),
            () => CanEditEndpoint);

        _disconnectCommand = new Command(
            async () => await DisconnectAsync(
                userInitiated: true),
            () =>
                IsConnected ||
                _connectionCancellation is not null);

        _sendCommand = new Command(
            async () => await SendAsync(),
            () =>
                IsConnected &&
                !IsSending &&
                !string.IsNullOrWhiteSpace(MessageText));

        _clearHistoryCommand = new Command(
            async () => await ClearHistoryAsync(),
            () => !IsBusy);

        Initialization = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        IsBusy = true;

        SetStatus(
            "Загрузка истории...",
            Colors.DarkOrange);

        try
        {
            IReadOnlyList<LogMessage> history =
                await _databaseService.GetAllLogsAsync();

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                Messages.Clear();

                foreach (LogMessage log in history)
                {
                    Messages.Add(log);
                }
            });

            SetStatus(
                "Нет подключения",
                Colors.Gray);
        }
        catch (Exception ex)
        {
            SetStatus(
                "Ошибка базы данных",
                Colors.Red);

            await AddSystemMessageAsync(
                $"Не удалось загрузить историю: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ConnectAsync()
    {
        if (!TryGetEndpoint(
                out IPAddress address,
                out int port,
                out string error))
        {
            ValidationMessage = error;
            return;
        }

        await _connectionLock.WaitAsync();

        try
        {
            if (IsConnected || IsBusy)
            {
                return;
            }

            IsBusy = true;
            ValidationMessage = string.Empty;

            SetStatus(
                "Подключение...",
                Colors.DarkOrange);

            await AddSystemMessageAsync(
                $"Подключение к {address}:{port}...");

            CleanupConnection();

            var connectionCancellation =
                new CancellationTokenSource();

            _connectionCancellation =
                connectionCancellation;

            RefreshCommandStates();

            var client = new TcpClient(
                address.AddressFamily)
            {
                NoDelay = true
            };

            _client = client;

            using var timeoutCancellation =
                CancellationTokenSource
                    .CreateLinkedTokenSource(
                        connectionCancellation.Token);

            timeoutCancellation.CancelAfter(
                ConnectionTimeout);

            await client.ConnectAsync(
                address,
                port,
                timeoutCancellation.Token);

            NetworkStream stream =
                client.GetStream();

            _stream = stream;

            IsConnected = true;

            SetStatus(
                $"Подключено: {address}:{port}",
                Colors.Green);

            await AddSystemMessageAsync(
                "Соединение установлено.");

            _ = ReceiveMessagesAsync(
                stream,
                connectionCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            bool wasCancelledByUser =
                _connectionCancellation
                    ?.IsCancellationRequested == true;

            CleanupConnection();

            IsConnected = false;

            if (wasCancelledByUser)
            {
                SetStatus(
                    "Подключение отменено",
                    Colors.Gray);

                await AddSystemMessageAsync(
                    "Подключение отменено.");
            }
            else
            {
                SetStatus(
                    "Тайм-аут подключения",
                    Colors.Red);

                await AddSystemMessageAsync(
                    $"Сервер не ответил за " +
                    $"{ConnectionTimeout.TotalSeconds:0} секунд.");
            }
        }
        catch (SocketException ex)
        {
            CleanupConnection();

            IsConnected = false;

            SetStatus(
                "Ошибка подключения",
                Colors.Red);

            await AddSystemMessageAsync(
                $"Ошибка сокета: {ex.Message}");
        }
        catch (Exception ex)
        {
            CleanupConnection();

            IsConnected = false;

            SetStatus(
                "Ошибка подключения",
                Colors.Red);

            await AddSystemMessageAsync(
                $"Не удалось подключиться: {ex.Message}");
        }
        finally
        {
            IsBusy = false;

            _connectionLock.Release();

            RefreshCommandStates();
        }
    }

    private async Task DisconnectAsync(
        bool userInitiated)
    {
        // отменяется ConnectAsync или ReadAsync
        try
        {
            _connectionCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // источник отмены уже освобождён
        }

        await _connectionLock.WaitAsync();

        try
        {
            bool hadActiveConnection =
                IsConnected ||
                _client is not null;

            CleanupConnection();

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                IsConnected = false;

                SetStatus(
                    "Нет подключения",
                    Colors.Gray);
            });

            if (userInitiated && hadActiveConnection)
            {
                await AddSystemMessageAsync(
                    "Соединение закрыто пользователем.");
            }
        }
        finally
        {
            _connectionLock.Release();

            RefreshCommandStates();
        }
    }

    private async Task SendAsync()
    {
        // не разрешаем две одновременные отправки
        if (!await _sendLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            IsSending = true;
            ValidationMessage = string.Empty;

            NetworkStream? stream = _stream;

            CancellationTokenSource? cancellation =
                _connectionCancellation;

            if (!IsConnected ||
                stream is null ||
                cancellation is null)
            {
                return;
            }

            string message =
                MessageText.TrimEnd('\r', '\n');

            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            // сервер воспринимат '\n' как конец отправленного текстового сообщения
            byte[] data =
                Encoding.UTF8.GetBytes(
                    message + "\n");

            await stream.WriteAsync(
                data.AsMemory(),
                cancellation.Token);

            MessageText = string.Empty;

            await SaveAndDisplayMessageAsync(
                message,
                isSentByUs: true);
        }
        catch (OperationCanceledException)
        {
            // отмена при отключении
        }
        catch (Exception ex)
            when (ex is IOException
                  or SocketException
                  or ObjectDisposedException)
        {
            await AddSystemMessageAsync(
                $"Ошибка отправки: {ex.Message}");

            await DisconnectAsync(
                userInitiated: false);
        }
        catch (Exception ex)
        {
            await AddSystemMessageAsync(
                $"Не удалось отправить сообщение: {ex.Message}");
        }
        finally
        {
            IsSending = false;

            _sendLock.Release();
        }
    }

    private async Task ReceiveMessagesAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        byte[] byteBuffer = new byte[4096];

        char[] charBuffer = new char[
            Encoding.UTF8.GetMaxCharCount(
                byteBuffer.Length)];

        Decoder decoder =
            Encoding.UTF8.GetDecoder();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(
                    byteBuffer.AsMemory(
                        0,
                        byteBuffer.Length),
                    cancellationToken);

                if (bytesRead == 0)
                {
                    await AddSystemMessageAsync(
                        "Сервер закрыл соединение.");

                    await DisconnectAsync(
                        userInitiated: false);

                    return;
                }

                int charactersRead = decoder.GetChars(
                    byteBuffer,
                    0,
                    bytesRead,
                    charBuffer,
                    0,
                    flush: false);

                if (charactersRead == 0)
                {
                    continue;
                }

                string response = new(
                    charBuffer,
                    0,
                    charactersRead);

                await SaveAndDisplayMessageAsync(
                    response,
                    isSentByUs: false);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            // штатное отключение
        }
        catch (ObjectDisposedException)
            when (cancellationToken.IsCancellationRequested)
        {
            // поток закрыт во время отключения
        }
        catch (Exception ex)
            when (ex is IOException
                  or SocketException
                  or ObjectDisposedException)
        {
            await AddSystemMessageAsync(
                $"Соединение потеряно: {ex.Message}");

            await DisconnectAsync(
                userInitiated: false);
        }
        catch (Exception ex)
        {
            await AddSystemMessageAsync(
                $"Ошибка получения данных: {ex.Message}");

            await DisconnectAsync(
                userInitiated: false);
        }
    }

    private async Task SaveAndDisplayMessageAsync(
        string text,
        bool isSentByUs)
    {
        var log = new LogMessage
        {
            Text = text,
            Timestamp = DateTime.Now,
            IsSentByUs = isSentByUs
        };

        try
        {
            await _databaseService.SaveLogAsync(log);
        }
        catch (Exception ex)
        {
            await AddSystemMessageAsync(
                "Не удалось сохранить сообщение в БД: " +
                ex.Message);
        }

        await AddMessageAsync(log);
    }

    private async Task ClearHistoryAsync()
    {
        try
        {
            await _databaseService.ClearLogsAsync();

            await MainThread.InvokeOnMainThreadAsync(
                () => Messages.Clear());

            await AddSystemMessageAsync(
                "История сообщений очищена.");
        }
        catch (Exception ex)
        {
            await AddSystemMessageAsync(
                $"Не удалось очистить историю: {ex.Message}");
        }
    }

    private bool TryGetEndpoint(
        out IPAddress address,
        out int port,
        out string error)
    {
        address = IPAddress.None;
        port = 0;

        string rawAddress =
            IpAddress.Trim();

        if (string.IsNullOrWhiteSpace(rawAddress))
        {
            error =
                "Введите IP-адрес сервера.";

            return false;
        }

        if (!IPAddress.TryParse(
                rawAddress,
                out IPAddress? parsedAddress))
        {
            error =
                "IP-адрес имеет неверный формат.";

            return false;
        }

        address = parsedAddress;

        if (!int.TryParse(Port, out port) ||
            port is < 1 or > 65535)
        {
            error =
                "Порт должен быть числом от 1 до 65535.";

            return false;
        }

        error = string.Empty;

        return true;
    }

    private Task AddSystemMessageAsync(
        string text)
    {
        return AddMessageAsync(
            new LogMessage
            {
                Text = text,
                Timestamp = DateTime.Now,
                IsSystem = true
            });
    }

    private Task AddMessageAsync(
        LogMessage message)
    {
        if (MainThread.IsMainThread)
        {
            Messages.Add(message);

            return Task.CompletedTask;
        }

        return MainThread.InvokeOnMainThreadAsync(
            () => Messages.Add(message));
    }

    private void SetStatus(
        string text,
        Color color)
    {
        StatusText = text;
        StatusColor = color;
    }

    private void CleanupConnection()
    {
        CancellationTokenSource? cancellation =
            _connectionCancellation;

        _connectionCancellation = null;

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // источник отмены уже освобождён
        }

        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // ошибки освобождения потока игнорируются
        }

        _stream = null;

        try
        {
            _client?.Dispose();
        }
        catch
        {
            // ошибки освобождения клиента игнорируются
        }

        _client = null;

        cancellation?.Dispose();

        RefreshCommandStates();
    }

    private void RefreshCommandStates()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(
                RefreshCommandStates);

            return;
        }

        _connectCommand.ChangeCanExecute();
        _disconnectCommand.ChangeCanExecute();
        _sendCommand.ChangeCanExecute();
        _clearHistoryCommand.ChangeCanExecute();
    }

    private bool SetProperty<T>(
        ref T storage,
        T value,
        [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(
                storage,
                value))
        {
            return false;
        }

        storage = value;

        OnPropertyChanged(propertyName);

        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string propertyName = "")
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        CleanupConnection();

        GC.SuppressFinalize(this);
    }
}