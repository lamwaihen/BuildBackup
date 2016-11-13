using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Services;
using Google.Apis.Upload;
using System;
using System.Collections.Generic;
using System.Diagnostics;
//using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Foundation.Diagnostics;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.System.Threading;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace BuildBackup
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        ThreadPoolTimer m_timerFolderCheck = null;
        IReadOnlyList<StorageFolder> m_folders;

        DriveService m_driveService = null;
        string m_buildBackupFolderId;

        public MainPage()
        {
            this.InitializeComponent();

            Initialize();
        }

        private async void Initialize()
        {
            // Google
            UserCredential credential = null;
            try
            {
                credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    new Uri("ms-appx:///Assets/client_secret.json"),
                    new[] { DriveService.Scope.Drive }, "user", CancellationToken.None);
            }
            catch (AggregateException ex)
            {
                Debug.Write("Credential failed, " + ex.Message);
            }

            // Create Drive API service.
            m_driveService = new DriveService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = "Corel Build Backup",
            });

            // Get Build_Backup folder
            FilesResource.ListRequest request = m_driveService.Files.List();
            request.Q = "mimeType = 'application/vnd.google-apps.folder' and 'root' in parents";
            request.Spaces = "drive";
            request.Fields = "files(id, name)";
            FileList result = request.Execute();
            foreach (File item in result.Files)
            {
                ComboBoxItem cbItem = new ComboBoxItem { Content = item.Name, Name = item.Id, Tag = item.Id };
                comboBoxGoogleFolders.Items.Add(cbItem);

                if (item.Name == "Build_Backup")
                    comboBoxGoogleFolders.SelectedItem = cbItem;
            }                    
        }

        private async void FolderCheckTimerElpasedHandler(ThreadPoolTimer timer, StorageFolder rootFolder)
        {
            if (timer != null)
            {
                timer.Cancel();
                timer = null;
            }

            await SyncGoogleDriveAsync(rootFolder, m_buildBackupFolderId, m_driveService);

            // Check again after 6 hours
            m_timerFolderCheck = ThreadPoolTimer.CreateTimer((newTimer) => FolderCheckTimerElpasedHandler(newTimer, rootFolder), TimeSpan.FromHours(6));
        }

        public async Task SyncGoogleDriveAsync(StorageFolder localFolder, string googleFolderId, DriveService googleDrive)
        {
            IReadOnlyList<IStorageItem> localItems = await localFolder.GetItemsAsync();
            foreach (IStorageItem item in localItems)
            {
                if (item.IsOfType(StorageItemTypes.Folder))
                {
                    UpdateStatus("Found folder:\t" + item.Path);

                    // If folder is build, but only contains 1 file (e.g. ISO, ZIP, EXE), simply go deeper
                    IReadOnlyList<IStorageItem> folderItems = await (item as StorageFolder).GetItemsAsync();

                    if (item.Name.Length > 12 && item.Name.Substring(item.Name.Length - 12).StartsWith("_LOGID")
                        && !(folderItems.Count == 1 && folderItems[0].IsOfType(StorageItemTypes.File)))
                    {
                        UpdateStatus("Folder:\t" + item.Name + " contains build.");
                        // If folder is build, check if zip exist in Google Drive
                        File uploadedFile = GoogleDriveIsItemExist(googleDrive, googleFolderId, item.Name + ".zip", "application/x-zip-compressed");
                        if (uploadedFile == null)
                        {
                            // Not exist, zip and upload
                            // Workaround: http://stackoverflow.com/questions/33801760/
                            await CopyFolderAsync(item as StorageFolder, ApplicationData.Current.TemporaryFolder, item.Name);
                            StorageFolder tempFolder = await StorageFolder.GetFolderFromPathAsync(ApplicationData.Current.TemporaryFolder.Path + "\\" + item.Name);
                            ZipFile.CreateFromDirectory(tempFolder.Path, ApplicationData.Current.TemporaryFolder.Path + "\\" + item.Name + ".zip", CompressionLevel.Optimal, false);
                            StorageFile tempFile = await StorageFile.GetFileFromPathAsync(ApplicationData.Current.TemporaryFolder.Path + "\\" + item.Name + ".zip");

                            // Upload to Google Drive
                            uploadedFile = await UploadAsync(googleDrive, new List<string> { googleFolderId }, tempFile);
                            UpdateStatus("Zip and uploaded:\t" + tempFile.Name + " as ID:" + uploadedFile.Id);

                            // Delete temp file and folder
                            await tempFolder.DeleteAsync(StorageDeleteOption.PermanentDelete);
                            await tempFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                        }

                        if (uploadedFile != null)
                        {
                            // If build is too old (180days), delete it.
                            if (DateTime.Now.Subtract(item.DateCreated.DateTime).Days > 180)
                            {
                                await item.DeleteAsync(StorageDeleteOption.PermanentDelete);
                                UpdateStatus("Deleted:\t" + item.Name + ", created on " + item.DateCreated.DateTime.ToString());
                            }
                        }
                    }
                    else
                    {
                        // Check if folder exist in Google Drive
                        File matchingGoogleFolder = GoogleDriveIsItemExist(googleDrive, googleFolderId, item.Name, "application/vnd.google-apps.folder");
                        if (matchingGoogleFolder == null)
                        {
                            // Not exist, create one
                            matchingGoogleFolder = await GoogleDriveCreateFolderAsync(googleDrive, googleFolderId, item.Name);
                        }
                        // Lets go deeper
                        await SyncGoogleDriveAsync(item as StorageFolder, matchingGoogleFolder.Id, googleDrive);
                    }
                }
                else
                {
                    UpdateStatus("Found file:\t" + item.Name);

                    // If item is file, check if exist in Google Drive.
                    File uploadedFile = GoogleDriveIsItemExist(googleDrive, googleFolderId, item.Name, (item as StorageFile).ContentType);
                    if (uploadedFile == null)
                    {
                        // Not exist, upload to Google Drive
                        StorageFile tempFile = await (item as StorageFile).CopyAsync(ApplicationData.Current.TemporaryFolder, item.Name, NameCollisionOption.ReplaceExisting);
                        uploadedFile = await UploadAsync(googleDrive, new List<string> { googleFolderId }, tempFile);
                        UpdateStatus("Uploaded:\t" + tempFile.Name + " as ID:" + uploadedFile.Id);
                        await tempFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }

                    if (uploadedFile != null)
                    {
                        // If build is too large (50MB) and too old (180days), delete it.
                        BasicProperties prop = await (item as StorageFile).GetBasicPropertiesAsync();
                        if (prop.Size > 50 * 1048576 && DateTime.Now.Subtract(item.DateCreated.DateTime).Days > 180)
                        {
                            await item.DeleteAsync(StorageDeleteOption.PermanentDelete);
                            UpdateStatus("Deleted:\t" + item.Name + ", created on " + item.DateCreated.DateTime.ToString() + " size " + string.Format("{0:N}%", (double)prop.Size / 1048576) + "MB.");
                        }
                    }
                }
            }
        }

        private void UpdateStatus(string status)
        {
            Utility.UIThreadExecute(() =>
            {
                textBoxStatus.Text += DateTime.Now.ToString() +"\t" + status + "\n";
            });
        }

        public static async Task<File> GoogleDriveCreateFolderAsync(DriveService driveService, string parent, string itemName)
        {
            File googleFolder = null;
            try
            {
                File folderMetadata = new File
                {
                    Name = itemName,
                    MimeType = "application/vnd.google-apps.folder",
                    Parents = new List<string> { parent }
                };
                FilesResource.CreateRequest requestUpload = driveService.Files.Create(folderMetadata);
                googleFolder = await requestUpload.ExecuteAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }

            return googleFolder;
        }

        public static File GoogleDriveIsItemExist(DriveService driveService, string parent, string itemName, string contentType)
        {
            File googleItem = null;

            switch (contentType)
            {
                case "application/msword": break;
                case "application/x-msdownload": contentType = "application/x-msdos-program"; break;
                case "application/x-zip-compressed": contentType = "application/zip"; break;
                case "application/vnd.google-apps.folder": break;
                case "image/png": break;
                case "image/jpeg": break;
                case "text/css": break;
                case "text/html": break;
                case "text/plain": break;
                case "text/xml": break;
                default: break;
            }

            try
            {
                FilesResource.ListRequest request = driveService.Files.List();
                request.Q = "name = '" + itemName + "'";
                request.Q += " and '" + parent + "' in parents";
                request.Q += string.IsNullOrWhiteSpace(contentType) ? "" : " and mimeType = '" + contentType + "'";
                request.Q += " and trashed = false";
                FileList result = request.Execute();
                if (result.Files.Count == 1)
                    googleItem = result.Files[0];
                else if (result.Files.Count > 1)
                { }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
            return googleItem;
        }

        public static async Task<File> UploadAsync(DriveService driveService, IList<string> parents, StorageFile file)
        {
            // Prepare the JSON metadata
            string json = "{\"name\":\"" + file.Name + "\"";
            if (parents.Count > 0)
            {
                json += ", \"parents\": [";
                foreach (string parent in parents)
                {
                    json += "\"" + parent + "\", ";
                }
                json = json.Remove(json.Length - 2) + "]";
            }
            json += "}";
            Debug.WriteLine(json);

            File uploadedFile = null;
            try
            {
                BasicProperties prop = await file.GetBasicPropertiesAsync();
                ulong fileSize = prop.Size;
                
                // Step 1: Start a resumable session
                HttpWebRequest httpRequest = (HttpWebRequest)WebRequest.Create("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable");
                httpRequest.Headers["Content-Type"] = "application /json; charset=UTF-8";
                httpRequest.Headers["Content-Length"] = json.Length.ToString();
                httpRequest.Headers["X-Upload-Content-Type"] = file.ContentType;
                httpRequest.Headers["X-Upload-Content-Length"] = fileSize.ToString();
                httpRequest.Headers["Authorization"] = "Bearer " + ((UserCredential)driveService.HttpClientInitializer).Token.AccessToken;
                httpRequest.Method = "POST";

                using (System.IO.Stream requestStream = await httpRequest.GetRequestStreamAsync())
                using (System.IO.StreamWriter streamWriter = new System.IO.StreamWriter(requestStream))
                {
                    streamWriter.Write(json);
                }

                // Step 2: Save the resumable session URI
                HttpWebResponse httpResponse = (HttpWebResponse)(await httpRequest.GetResponseAsync());
                if (httpResponse.StatusCode != HttpStatusCode.OK)
                    return null;

                // Step 3: Upload the file
                httpRequest = (HttpWebRequest)WebRequest.Create("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=" + httpResponse.Headers["x-guploader-uploadid"]);
                httpRequest.Headers["Content-Type"] = file.ContentType;
                httpRequest.Headers["Content-Length"] = fileSize.ToString();
                httpRequest.Method = "PUT";

                using (System.IO.Stream requestStream = await httpRequest.GetRequestStreamAsync())
                using (System.IO.FileStream fileStream = new System.IO.FileStream(file.Path, System.IO.FileMode.Open))
                {
                    await fileStream.CopyToAsync(requestStream);
                }

                httpResponse = (HttpWebResponse)(await httpRequest.GetResponseAsync());
                if (httpResponse.StatusCode != HttpStatusCode.OK)
                    return null;

                // Try to retrieve the file from Google
                FilesResource.ListRequest request = driveService.Files.List();
                if (parents.Count > 0)
                    request.Q += "'" + parents[0] + "' in parents and ";
                request.Q += "name = '" + file.Name + "'";
                FileList result = request.Execute();
                if (result.Files.Count > 0)
                    uploadedFile = result.Files[0];
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }

            return uploadedFile;
        }

        public static async Task CopyFolderAsync(StorageFolder source, StorageFolder destinationContainer, string desiredName = null)
        {
            StorageFolder destinationFolder = null;
            destinationFolder = await destinationContainer.CreateFolderAsync(
                desiredName ?? source.Name, CreationCollisionOption.ReplaceExisting);

            foreach (var file in await source.GetFilesAsync())
            {
                await file.CopyAsync(destinationFolder, file.Name, NameCollisionOption.ReplaceExisting);
            }
            foreach (var folder in await source.GetFoldersAsync())
            {
                await CopyFolderAsync(folder, destinationFolder);
            }
        }

        private void textBoxStatus_TextChanged(object sender, TextChangedEventArgs e)
        {
            var grid = (Grid)VisualTreeHelper.GetChild(textBoxStatus, 0);
            for (var i = 0; i <= VisualTreeHelper.GetChildrenCount(grid) - 1; i++)
            {
                object obj = VisualTreeHelper.GetChild(grid, i);
                if (!(obj is ScrollViewer)) continue;
                ((ScrollViewer)obj).ChangeView(0.0f, ((ScrollViewer)obj).ExtentHeight, 1.0f);
                break;
            }
        }

        private void comboBoxGoogleFolders_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            m_buildBackupFolderId = (comboBoxGoogleFolders.SelectedItem as ComboBoxItem).Tag.ToString();
        }

        private async void buttonStartBackup_Click(object sender, RoutedEventArgs e)
        {
            FolderPicker folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            folderPicker.FileTypeFilter.Add("*");
            StorageFolder folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                m_folders = await folder.GetFoldersAsync();

                // Create a timer to periodically check the build folders
                m_timerFolderCheck = ThreadPoolTimer.CreateTimer((timer) => FolderCheckTimerElpasedHandler(timer, folder), TimeSpan.FromMilliseconds(10));
            }
        }
    }
}
