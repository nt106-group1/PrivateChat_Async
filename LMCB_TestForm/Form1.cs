using System;
using System.Data.Odbc;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LMCB_TestForm
{
    public partial class Form1 : Form
    {
        private TcpClient tcpClient;
        private StreamWriter sWriter;
        private CancellationTokenSource cancellationTokenSource; // Sử dụng CancellationTokenSource để hủy bỏ các tác vụ async
        private int serverPort = 8000;
        public const int BufferSize = 4096;
        public const int FileBufferSize = 3072;
        private string SaveFileName = string.Empty;
        private MemoryStream fileSaveMemoryStream;

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
        /// Phương thức nhận dữ liệu bất đồng bộ từ NetworkStream.
        /// Thay thế phương thức sử dụng Thread trước đây.
        /// </summary>
        private async Task ReceiveDataAsync(NetworkStream networkStream, CancellationToken cancellationToken)
        {
            byte[] readBuffer = new byte[BufferSize];

            try
            {
                while (!cancellationToken.IsCancellationRequested && tcpClient.Connected)
                {
                    // Đọc dữ liệu một cách bất đồng bộ từ NetworkStream
                    int bytesRead = await networkStream.ReadAsync(readBuffer, 0, BufferSize, cancellationToken);

                    if (bytesRead == 0)
                    {
                        // Kết nối đã bị đóng
                        break;
                    }

                    string headerAndMessage = Encoding.UTF8.GetString(readBuffer, 0, bytesRead).Replace("\0", string.Empty);
                    string[] arrPayload = headerAndMessage.Split(';');

                    if (arrPayload.Length >= 3)
                    {
                        string senderUsername = arrPayload[0];
                        if (!Enum.TryParse(arrPayload[1], true, out MessageType msgType))
                        {
                            // Loại thông điệp không xác định
                            continue;
                        }

                        string content = arrPayload[2].Replace("\0", string.Empty);

                        switch (msgType)
                        {
                            case MessageType.Text:
                                string formattedMsg = $"{senderUsername}: {content}\n";
                                UpdateChatHistoryThreadSafe(formattedMsg);
                                break;

                            case MessageType.FilePart:
                                await HandleFilePartAsync(content);
                                break;

                            case MessageType.FileEof:
                                await HandleFileEofAsync(content);
                                break;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Hoạt động bị hủy bỏ, có thể bỏ qua
            }
            catch (Exception ex)
            {
                // Xử lý các ngoại lệ khác (ví dụ: lỗi mạng)
                MessageBox.Show($"Error receiving data: {ex.Message}");
            }
            finally
            {
                CloseConnection(); // Đảm bảo kết nối được đóng khi có lỗi hoặc hoàn thành
            }
        }

        /// <summary>
        /// Xử lý phần dữ liệu file được gửi từ server.
        /// </summary>
        private async Task HandleFilePartAsync(string content)
        {
            if (string.IsNullOrEmpty(SaveFileName))
            {
                // Hiển thị hộp thoại xác nhận nhận file
                DialogResult result = InvokeRequired
                    ? (DialogResult)Invoke(new Func<DialogResult>(() =>
                        MessageBox.Show("Receive incoming file ", "File sent request", MessageBoxButtons.YesNo)))
                    : MessageBox.Show("Receive incoming file ", "File sent request", MessageBoxButtons.YesNo);

                if (result == DialogResult.Yes)
                {
                    using (SaveFileDialog dialogSave = new SaveFileDialog())
                    {
                        dialogSave.Filter = "All files (*.*)|*.*";
                        dialogSave.RestoreDirectory = true;
                        dialogSave.Title = "Where do you want to save the file?";
                        dialogSave.InitialDirectory = @"C:/";

                        if (InvokeRequired)
                        {
                            var dialogResult = (DialogResult)Invoke(new Func<DialogResult>(() => dialogSave.ShowDialog()));
                            if (dialogResult == DialogResult.OK)
                            {
                                SaveFileName = dialogSave.FileName;
                                fileSaveMemoryStream = new MemoryStream();
                            }
                        }
                        else
                        {
                            if (dialogSave.ShowDialog() == DialogResult.OK)
                            {
                                SaveFileName = dialogSave.FileName;
                                fileSaveMemoryStream = new MemoryStream();
                            }
                        }
                    }
                }
            }

            byte[] filePart = Encoding.UTF8.GetBytes(content);
            fileSaveMemoryStream?.Write(filePart, 0, filePart.Length);
        }

        /// <summary>
        /// Xử lý phần kết thúc của file được gửi từ server.
        /// </summary>
        private async Task HandleFileEofAsync(string content)
        {
            if (string.IsNullOrEmpty(SaveFileName))
            {
                // Hiển thị hộp thoại xác nhận nhận file
                DialogResult result = InvokeRequired
                    ? (DialogResult)Invoke(new Func<DialogResult>(() =>
                        MessageBox.Show("Receive incoming file ", "File sent request", MessageBoxButtons.YesNo)))
                    : MessageBox.Show("Receive incoming file ", "File sent request", MessageBoxButtons.YesNo);

                if (result == DialogResult.Yes)
                {
                    using (SaveFileDialog dialogSave = new SaveFileDialog())
                    {
                        dialogSave.Filter = "All files (*.*)|*.*";
                        dialogSave.RestoreDirectory = true;
                        dialogSave.Title = "Where do you want to save the file?";
                        dialogSave.InitialDirectory = @"C:/";

                        if (InvokeRequired)
                        {
                            var dialogResult = (DialogResult)Invoke(new Func<DialogResult>(() => dialogSave.ShowDialog()));
                            if (dialogResult == DialogResult.OK)
                            {
                                SaveFileName = dialogSave.FileName;
                            }
                        }
                        else
                        {
                            if (dialogSave.ShowDialog() == DialogResult.OK)
                            {
                                SaveFileName = dialogSave.FileName;
                            }
                        }
                    }
                }
            }

            byte[] finalFilePart = Encoding.UTF8.GetBytes(content);

            if (fileSaveMemoryStream != null)
            {
                fileSaveMemoryStream.Write(finalFilePart, 0, finalFilePart.Length);
                using (FileStream fs = File.OpenWrite(SaveFileName))
                {
                    fileSaveMemoryStream.Seek(0, SeekOrigin.Begin);
                    await fileSaveMemoryStream.CopyToAsync(fs); // Ghi dữ liệu từ MemoryStream vào FileStream một cách bất đồng bộ
                }
            }
            else
            {
                using (FileStream fs = File.OpenWrite(SaveFileName))
                {
                    await fs.WriteAsync(finalFilePart, 0, finalFilePart.Length); // Ghi dữ liệu cuối cùng vào file một cách bất đồng bộ
                }
            }

            fileSaveMemoryStream = null;
            SaveFileName = null;
        }

        private delegate void SafeCallDelegate(string text);

        /// <summary>
        /// Cập nhật lịch sử chat một cách an toàn trên luồng UI.
        /// </summary>
        private void UpdateChatHistoryThreadSafe(string text)
        {
            if (richTextBox1.InvokeRequired)
            {
                var d = new SafeCallDelegate(UpdateChatHistoryThreadSafe);
                richTextBox1.Invoke(d, new object[] { text });
            }
            else
            {
                richTextBox1.AppendText(text); // Sử dụng AppendText thay vì +=
            }
        }

        /// <summary>
        /// Xử lý sự kiện nhấn nút gửi tin nhắn.
        /// Được cập nhật để sử dụng async/await.
        /// </summary>
        private async void button1_Click(object sender, EventArgs e)
        {
            try
            {
                if (tcpClient?.Connected ?? false)
                {
                    NetworkStream ns = tcpClient.GetStream();
                    string allInOneMsg = $"{textBox3.Text};{MessageType.Text};{sendMsgTextBox.Text}";
                    byte[] sendingBytes = Encoding.UTF8.GetBytes(allInOneMsg);
                    await ns.WriteAsync(sendingBytes, 0, sendingBytes.Length); // Ghi dữ liệu
                    sendMsgTextBox.Text = "";
                }
                else
                {
                    MessageBox.Show("Not connected to the server.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error sending message: {ex.Message}");
            }
        }

        /// <summary>
        /// Xử lý sự kiện nhấn nút kết nối.
        /// Được cập nhật để sử dụng async/await.
        /// </summary>
        private async void button2_Click(object sender, EventArgs e)
        {
            try
            {
                cancellationTokenSource = new CancellationTokenSource(); // Khởi tạo CancellationTokenSource mới

                tcpClient = new TcpClient();
                await tcpClient.ConnectAsync(IPAddress.Parse(textBox2.Text), serverPort); // Kết nối
                NetworkStream networkStream = tcpClient.GetStream();

                sWriter = new StreamWriter(networkStream) { AutoFlush = true };
                await sWriter.WriteLineAsync(textBox1.Text); // Ghi dữ liệu 

                // Bắt đầu nhận dữ liệu
                _ = ReceiveDataAsync(networkStream, cancellationTokenSource.Token);

                MessageBox.Show("Connected");
            }
            catch (SocketException sockEx)
            {
                MessageBox.Show(sockEx.Message, "Network error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        /// <summary>
        /// Phương thức đóng kết nối và hủy bỏ các tác vụ async.
        /// </summary>
        private void CloseConnection()
        {
            if (tcpClient != null)
            {
                if (tcpClient.Connected)
                {
                    tcpClient.Close();
                }
                tcpClient = null;
            }

            if (cancellationTokenSource != null)
            {
                cancellationTokenSource.Cancel(); // Hủy bỏ các tác vụ đang chạy
                cancellationTokenSource.Dispose();
                cancellationTokenSource = null;
            }
        }

        /// <summary>
        /// Xử lý sự kiện chọn "Open" từ menu để gửi file.
        /// Được cập nhật để sử dụng async/await.
        /// </summary>
        private async void openToolStripMenuItem_Click(object sender, EventArgs e)
        {
            string filePath = string.Empty;
            byte[] sendingBuffer = null;
            try
            {
                if (tcpClient?.Connected ?? false)
                {
                    NetworkStream networkStream = tcpClient.GetStream();
                    using (OpenFileDialog openFileDialog = new OpenFileDialog())
                    {
                        openFileDialog.InitialDirectory = "c:\\";
                        openFileDialog.Filter = "txt files (*.txt)|*.txt|All files (*.*)|*.*";
                        openFileDialog.FilterIndex = 2;
                        openFileDialog.RestoreDirectory = true;

                        if (openFileDialog.ShowDialog() == DialogResult.OK)
                        {
                            filePath = openFileDialog.FileName;
                            using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                            {
                                int NoOfPackets = (int)Math.Ceiling((double)fileStream.Length / FileBufferSize);
                                progressBar1.Maximum = NoOfPackets;
                                int TotalLength = (int)fileStream.Length;
                                int CurrentPacketLength, counter = 0;

                                for (int i = 0; i < NoOfPackets; i++)
                                {
                                    if (TotalLength > FileBufferSize)
                                    {
                                        CurrentPacketLength = FileBufferSize;
                                        TotalLength -= CurrentPacketLength;
                                    }
                                    else
                                    {
                                        CurrentPacketLength = TotalLength;
                                    }

                                    byte[] fileBuffer = new byte[CurrentPacketLength];
                                    int bytesRead = await fileStream.ReadAsync(fileBuffer, 0, CurrentPacketLength); // Đọc dữ liệu file 

                                    MessageType msgType = (i == NoOfPackets - 1) ? MessageType.FileEof : MessageType.FilePart;
                                    string header = $"{textBox3.Text};{msgType};";
                                    byte[] headerBytes = Encoding.UTF8.GetBytes(header);
                                    byte[] sendingBytes = headerBytes.Concat(fileBuffer).ToArray();

                                    await networkStream.WriteAsync(sendingBytes, 0, sendingBytes.Length); // Gửi dữ liệu 
                                    progressBar1.Invoke((Action)(() => progressBar1.PerformStep())); // Cập nhật progress bar 
                                }
                            }

                            MessageBox.Show("File sent successfully.");
                        }
                    }
                }
                else
                {
                    MessageBox.Show("Not connected to the server.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error sending file: {ex.Message}");
            }
        }

        /// <summary>
        /// Xử lý sự kiện đóng form để đảm bảo kết nối được đóng đúng cách.
        /// </summary>
        private void Form1_Close(object sender, FormClosingEventArgs e)
        {
            CloseConnection();
        }
    }
}
