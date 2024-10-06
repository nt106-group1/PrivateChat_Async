using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ServerChatForm
{
    public partial class Form1 : Form
    {
        private TcpListener tcpListener;
        private CancellationTokenSource cancellationTokenSource; // Sử dụng để hủy bỏ các tác vụ bất đồng bộ
        private readonly int _serverPort = 8000;
        private ConcurrentDictionary<string, TcpClient> dict = new ConcurrentDictionary<string, TcpClient>(); // Sử dụng ConcurrentDictionary để an toàn trên nhiều luồng
        public const int BufferSize = 4096;

        public enum MessageType
        {
            Text,
            FileEof,
            FilePart,
        }

        public Form1()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Phương thức khởi động máy chủ và bắt đầu lắng nghe kết nối một cách bất đồng bộ.
        /// </summary>
        private async Task StartListeningAsync()
        {
            try
            {
                // Khởi tạo TcpListener với địa chỉ IP và cổng đã định
                IPAddress ipAddress = IPAddress.Parse(textBox1.Text);
                tcpListener = new TcpListener(ipAddress, _serverPort);
                tcpListener.Start();
                AppendChatHistory($"Máy chủ đang lắng nghe trên {ipAddress}:{_serverPort}\n");

                cancellationTokenSource = new CancellationTokenSource();
                CancellationToken token = cancellationTokenSource.Token;

                // Vòng lặp lắng nghe kết nối mới
                while (!token.IsCancellationRequested)
                {
                    // Chấp nhận kết nối một cách bất đồng bộ
                    TcpClient client = await tcpListener.AcceptTcpClientAsync().ConfigureAwait(false);
                    _ = HandleNewClientAsync(client, token); // Xử lý khách hàng mới mà không chặn vòng lặp lắng nghe
                }
            }
            catch (ObjectDisposedException)
            {
                // TcpListener đã bị đóng, không làm gì cả
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi khi lắng nghe kết nối: {ex.Message}");
            }
        }

        /// <summary>
        /// Phương thức xử lý khách hàng mới một cách bất đồng bộ.
        /// </summary>
        private async Task HandleNewClientAsync(TcpClient client, CancellationToken token)
        {
            string username = string.Empty;
            try
            {
                NetworkStream networkStream = client.GetStream();
                using (StreamReader sr = new StreamReader(networkStream, Encoding.UTF8, false, BufferSize, true))
                using (StreamWriter sw = new StreamWriter(networkStream, Encoding.UTF8, BufferSize, true) { AutoFlush = true })
                {
                    // Đọc tên người dùng từ khách hàng
                    username = await sr.ReadLineAsync().ConfigureAwait(false);
                    if (string.IsNullOrEmpty(username))
                    {
                        await sw.WriteLineAsync("Please pick a username").ConfigureAwait(false);
                        client.Close();
                        return;
                    }

                    // Kiểm tra xem tên người dùng đã tồn tại chưa
                    if (!dict.ContainsKey(username))
                    {
                        if (dict.TryAdd(username, client))
                        {
                            await sw.WriteLineAsync("Username accepted").ConfigureAwait(false);
                            AppendChatHistory($"Người dùng '{username}' đã kết nối.\n");
                            // Bắt đầu xử lý dữ liệu từ khách hàng
                            await HandleClientAsync(username, client, token).ConfigureAwait(false);
                        }
                        else
                        {
                            await sw.WriteLineAsync("Username already exist, pick another one").ConfigureAwait(false);
                            client.Close();
                        }
                    }
                    else
                    {
                        await sw.WriteLineAsync("Username already exist, pick another one").ConfigureAwait(false);
                        client.Close();
                    }
                }
            }
            catch (IOException)
            {
                // Kết nối bị đóng đột ngột
                if (!string.IsNullOrEmpty(username))
                {
                    RemoveClient(username);
                    AppendChatHistory($"Người dùng '{username}' đã ngắt kết nối.\n");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi khi xử lý khách hàng '{username}': {ex.Message}");
            }
        }

        /// <summary>
        /// Phương thức xử lý dữ liệu từ khách hàng một cách bất đồng bộ.
        /// </summary>
        private async Task HandleClientAsync(string username, TcpClient client, CancellationToken token)
        {
            try
            {
                NetworkStream networkStream = client.GetStream();
                using (StreamReader sr = new StreamReader(networkStream, Encoding.UTF8, false, BufferSize, true))
                using (StreamWriter sw = new StreamWriter(networkStream, Encoding.UTF8, BufferSize, true) { AutoFlush = true })
                {
                    byte[] buffer = new byte[BufferSize];

                    while (!token.IsCancellationRequested && client.Connected)
                    {
                        // Đọc dữ liệu từ khách hàng một cách bất đồng bộ
                        int bytesRead = await networkStream.ReadAsync(buffer, 0, BufferSize, token).ConfigureAwait(false);
                        if (bytesRead == 0)
                        {
                            // Kết nối đã bị đóng
                            break;
                        }

                        string headerAndMessage = Encoding.UTF8.GetString(buffer, 0, bytesRead).TrimEnd('\0');
                        string[] arrPayload = headerAndMessage.Split(';');

                        if (arrPayload.Length >= 3)
                        {
                            string friendUsername = arrPayload[0];
                            if (!Enum.TryParse(arrPayload[1], true, out MessageType msgType))
                            {
                                // Loại thông điệp không xác định
                                continue;
                            }

                            string content = arrPayload[2].TrimEnd('\0');
                            string forwardHeaderAndMessage = $"{username};{msgType};{content}";

                            if (msgType == MessageType.Text)
                            {
                                // Xử lý tin nhắn văn bản
                                if (dict.TryGetValue(friendUsername, out TcpClient friendTcpClient))
                                {
                                    await SendMessageAsync(friendTcpClient, forwardHeaderAndMessage).ConfigureAwait(false);
                                }

                                // Gửi lại tin nhắn cho người gửi
                                await SendMessageAsync(client, forwardHeaderAndMessage).ConfigureAwait(false);
                                AppendChatHistory($"[{username} -> {friendUsername}]: {content}\n");
                            }
                            else if (msgType == MessageType.FilePart || msgType == MessageType.FileEof)
                            {
                                // Xử lý phần file hoặc kết thúc file
                                if (dict.TryGetValue(friendUsername, out TcpClient friendTcpClient))
                                {
                                    await SendBytesAsync(friendTcpClient.GetStream(), buffer, bytesRead).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                }
            }
            catch (IOException)
            {
                // Kết nối bị đóng đột ngột
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi khi xử lý dữ liệu từ '{username}': {ex.Message}");
            }
            finally
            {
                // Khi kết thúc, loại bỏ khách hàng khỏi danh sách
                RemoveClient(username);
                AppendChatHistory($"Người dùng '{username}' đã ngắt kết nối.\n");
                client.Close();
            }
        }

        /// <summary>
        /// Gửi tin nhắn văn bản đến khách hàng một cách bất đồng bộ.
        /// </summary>
        private async Task SendMessageAsync(TcpClient client, string message)
        {
            try
            {
                NetworkStream ns = client.GetStream();
                byte[] messageBytes = Encoding.UTF8.GetBytes(message + "\n");
                await ns.WriteAsync(messageBytes, 0, messageBytes.Length).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi khi gửi tin nhắn: {ex.Message}");
            }
        }

        /// <summary>
        /// Gửi mảng byte đến khách hàng một cách bất đồng bộ.
        /// </summary>
        private async Task SendBytesAsync(NetworkStream ns, byte[] data, int count)
        {
            try
            {
                await ns.WriteAsync(data, 0, count).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi khi gửi dữ liệu: {ex.Message}");
            }
        }

        /// <summary>
        /// Loại bỏ khách hàng khỏi danh sách khi họ ngắt kết nối.
        /// </summary>
        private void RemoveClient(string username)
        {
            if (dict.TryRemove(username, out TcpClient removedClient))
            {
                removedClient.Close();
            }
        }

        private delegate void SafeCallDelegate(string text);

        /// <summary>
        /// Cập nhật lịch sử chat một cách an toàn trên luồng UI.
        /// </summary>
        private void AppendChatHistory(string text)
        {
            if (richTextBox1.InvokeRequired)
            {
                var d = new SafeCallDelegate(AppendChatHistory);
                richTextBox1.Invoke(d, new object[] { text });
            }
            else
            {
                richTextBox1.AppendText(text);
            }
        }

        /// <summary>
        /// Xử lý sự kiện nhấn nút bắt đầu/dừng máy chủ.
        /// </summary>
        private async void button1_Click(object sender, EventArgs e)
        {
            if (cancellationTokenSource == null)
            {
                // Bắt đầu máy chủ
                try
                {
                    button1.Text = "Stop";
                    AppendChatHistory("Bắt đầu máy chủ...\n");
                    await StartListeningAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Lỗi khi khởi động máy chủ: {ex.Message}");
                }
            }
            else
            {
                // Dừng máy chủ
                try
                {
                    cancellationTokenSource.Cancel();
                    tcpListener.Stop();
                    cancellationTokenSource = null;
                    button1.Text = "Start Listening";
                    AppendChatHistory("Máy chủ đã dừng.\n");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Lỗi khi dừng máy chủ: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Phương thức đóng kết nối khi đóng form.
        /// </summary>
        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (cancellationTokenSource != null)
            {
                cancellationTokenSource.Cancel();
            }

            foreach (var client in dict.Values)
            {
                client.Close();
            }

            tcpListener?.Stop();
        }
    }
}
