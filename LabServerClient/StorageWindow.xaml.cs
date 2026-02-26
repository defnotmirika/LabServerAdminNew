using Microsoft.VisualBasic;
using Microsoft.Win32;
using Npgsql;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace LabServerClient
{
    public partial class StorageWindow : Window
    {
        private readonly int _ownerId;
        private readonly string? _connectionString;
        public ObservableCollection<StorageFileItem> Files { get; } = new();

        public StorageWindow(int ownerId, string? connectionString)
        {
            _ownerId = ownerId;
            _connectionString = connectionString;
            DataContext = this;
            InitializeComponent();
            OwnerText.Text = $"Owner ID: {_ownerId}";
            FilesGrid.ItemsSource = Files;
            Loaded += StorageWindow_Loaded;
        }

        private async void StorageWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_connectionString))
            {
                MessageBox.Show("Storage is unavailable because the database connection is not configured.", "Storage", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            await EnsureTableAsync();
            await LoadFilesAsync();
        }

        private async Task EnsureTableAsync()
        {
            try
            {
                using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();
                var sql = @"
                    CREATE TABLE IF NOT EXISTS client_files (
                        id SERIAL PRIMARY KEY,
                        owner_id INTEGER NOT NULL REFERENCES us_credentials(id) ON DELETE CASCADE,
                        filename TEXT NOT NULL,
                        filetype TEXT,
                        filesize BIGINT,
                        filedata BYTEA,
                        upload_date TIMESTAMPTZ DEFAULT NOW()
                    );
                    CREATE INDEX IF NOT EXISTS idx_client_files_owner ON client_files(owner_id);
                ";
                using var cmd = new NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize storage table: {ex.Message}", "Storage", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private async Task LoadFilesAsync()
        {
            Files.Clear();
            try
            {
                using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();
                var sql = "SELECT id, filename, filetype, filesize, upload_date FROM client_files WHERE owner_id = @owner ORDER BY upload_date DESC";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@owner", _ownerId);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    Files.Add(new StorageFileItem
                    {
                        Id = reader.GetInt32(reader.GetOrdinal("id")),
                        FileName = reader.GetString(reader.GetOrdinal("filename")),
                        FileType = reader.IsDBNull(reader.GetOrdinal("filetype")) ? string.Empty : reader.GetString(reader.GetOrdinal("filetype")),
                        FileSize = reader.IsDBNull(reader.GetOrdinal("filesize")) ? 0 : reader.GetInt64(reader.GetOrdinal("filesize")),
                        UploadDate = reader.GetDateTime(reader.GetOrdinal("upload_date"))
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load files: {ex.Message}", "Storage", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task UploadFilesAsync(string[] paths)
        {
            if (paths == null || paths.Length == 0)
            {
                return;
            }

            try
            {
                using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();

                foreach (var path in paths)
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    var fileInfo = new FileInfo(path);
                    await using var fs = File.OpenRead(path);
                    await using var ms = new MemoryStream();
                    await fs.CopyToAsync(ms);
                    var data = ms.ToArray();

                    var sql = @"INSERT INTO client_files (owner_id, filename, filetype, filesize, filedata, upload_date)
                                VALUES (@owner, @name, @type, @size, @data, NOW())";
                    using var cmd = new NpgsqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@owner", _ownerId);
                    cmd.Parameters.AddWithValue("@name", fileInfo.Name);
                    cmd.Parameters.AddWithValue("@type", (object)(fileInfo.Extension.TrimStart('.')) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@size", fileInfo.Length);
                    cmd.Parameters.AddWithValue("@data", data);
                    await cmd.ExecuteNonQueryAsync();
                }

                await LoadFilesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Upload failed: {ex.Message}", "Storage", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task DownloadFileAsync(StorageFileItem item)
        {
            if (item == null) return;
            try
            {
                using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();
                var sql = "SELECT filedata, filename FROM client_files WHERE owner_id = @owner AND id = @id LIMIT 1";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@owner", _ownerId);
                cmd.Parameters.AddWithValue("@id", item.Id);

                using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    MessageBox.Show("File not found.", "Storage", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var data = (byte[])reader["filedata"];
                var filename = reader.GetString(reader.GetOrdinal("filename"));

                var dialog = new SaveFileDialog
                {
                    FileName = filename,
                    Filter = "All files (*.*)|*.*"
                };

                if (dialog.ShowDialog() == true)
                {
                    await File.WriteAllBytesAsync(dialog.FileName, data);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Download failed: {ex.Message}", "Storage", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task RenameFileAsync(StorageFileItem item)
        {
            if (item == null) return;
            var newName = Interaction.InputBox("Enter new file name", "Rename File", item.FileName);
            if (string.IsNullOrWhiteSpace(newName) || newName == item.FileName)
            {
                return;
            }

            try
            {
                using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();
                var sql = "UPDATE client_files SET filename = @name WHERE owner_id = @owner AND id = @id";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@name", newName);
                cmd.Parameters.AddWithValue("@owner", _ownerId);
                cmd.Parameters.AddWithValue("@id", item.Id);
                await cmd.ExecuteNonQueryAsync();
                await LoadFilesAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Rename failed: {ex.Message}", "Storage", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task DeleteFileAsync(StorageFileItem item)
        {
            if (item == null) return;
            var confirm = MessageBox.Show($"Delete {item.FileName}?", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();
                var sql = "DELETE FROM client_files WHERE owner_id = @owner AND id = @id";
                using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@owner", _ownerId);
                cmd.Parameters.AddWithValue("@id", item.Id);
                await cmd.ExecuteNonQueryAsync();
                Files.Remove(item);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Delete failed: {ex.Message}", "Storage", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Multiselect = true,
                Filter = "All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                await UploadFilesAsync(dialog.FileNames);
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadFilesAsync();
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is StorageFileItem item)
            {
                await DownloadFileAsync(item);
            }
        }

        private async void RenameButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is StorageFileItem item)
            {
                await RenameFileAsync(item);
            }
        }

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is StorageFileItem item)
            {
                await DeleteFileAsync(item);
            }
        }

        private async void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                await UploadFilesAsync(files);
            }
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }
    }

    public class StorageFileItem
    {
        public int Id { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string FileType { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public DateTime UploadDate { get; set; }
        public string FormattedSize
        {
            get
            {
                var size = FileSize;
                if (size >= 1024 * 1024)
                    return $"{size / 1024f / 1024f:0.##} MB";
                if (size >= 1024)
                    return $"{size / 1024f:0.##} KB";
                return $"{size} B";
            }
        }
    }
}
