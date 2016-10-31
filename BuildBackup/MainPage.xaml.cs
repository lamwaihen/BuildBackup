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
using Windows.Storage;
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
            FolderPicker folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            folderPicker.FileTypeFilter.Add("*");
            StorageFolder folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                m_folders = await folder.GetFoldersAsync();

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
                request.Q = "mimeType = 'application/vnd.google-apps.folder' and name = 'Build_Backup'";
                request.Spaces = "drive";
                request.Fields = "files(id, name)";
                FileList result = request.Execute();
                m_buildBackupFolderId = result.Files[0].Id;

                // Create a timer to periodically check the build folders
                m_timerFolderCheck = ThreadPoolTimer.CreateTimer((timer) => FolderCheckTimerElpasedHandler(timer, folder), TimeSpan.FromMilliseconds(10));
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

        public static async Task SyncGoogleDriveAsync(StorageFolder localFolder, string googleFolderId, DriveService googleDrive)
        {
            IReadOnlyList<IStorageItem> localItems = await localFolder.GetItemsAsync();
            foreach (IStorageItem item in localItems)
            {
                if (item.IsOfType(StorageItemTypes.Folder))
                {
                    if (item.Name.Length > 12 && item.Name.Substring(item.Name.Length - 12).StartsWith("_LOGID"))
                    {
                        // If folder is build, check if zip exist in Google Drive
                        FilesResource.ListRequest request = googleDrive.Files.List();
                        request.Q = "'" + googleFolderId + "' in parents and mimeType != 'application/vnd.google-apps.folder' and name contains '" + item.Name + "'";
                        FileList result = request.Execute();
                        if (result.Files.Count == 0)
                        {
                            // Not exist, zip and upload
                            // Workaround: http://stackoverflow.com/questions/33801760/
                            await CopyFolderAsync(item as StorageFolder, ApplicationData.Current.TemporaryFolder, item.Name);
                            StorageFolder tempFolder = await StorageFolder.GetFolderFromPathAsync(ApplicationData.Current.TemporaryFolder.Path + "\\" + item.Name);
                            ZipFile.CreateFromDirectory(tempFolder.Path, ApplicationData.Current.TemporaryFolder.Path + "\\" + item.Name + ".zip", CompressionLevel.Optimal, false);
                            StorageFile tempFile = await StorageFile.GetFileFromPathAsync(ApplicationData.Current.TemporaryFolder.Path + "\\" + item.Name + ".zip");

                            // Upload to Google Drive
                            File fileMetadata = new File
                            {
                                Name = tempFile.Name,
                                MimeType = tempFile.ContentType,
                                Parents = new List<string> { googleFolderId }
                            };
                            FilesResource.CreateMediaUpload requestUpload;

                            using (System.IO.FileStream stream = new System.IO.FileStream(tempFile.Path, System.IO.FileMode.Open))
                            {
                                requestUpload = googleDrive.Files.Create(fileMetadata, stream, tempFile.ContentType);
                                requestUpload.Fields = "id";
                                requestUpload.ProgressChanged += videosInsertRequest_ProgressChanged;
                                requestUpload.ResponseReceived += videosInsertRequest_ResponseReceived;
                                await requestUpload.UploadAsync();
                                File uploadedFile = requestUpload.ResponseBody;
                            }
                            await tempFolder.DeleteAsync(StorageDeleteOption.PermanentDelete);
                            await tempFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                        }

                        // If build is too old, delete it.
                        TimeSpan timeDiff = DateTime.Now.Subtract(item.DateCreated.DateTime);
                    }
                    else
                    {
                        // Check if folder exist in Google Drive
                        FilesResource.ListRequest request = googleDrive.Files.List();
                        request.Q = "'" + googleFolderId + "' in parents and mimeType = 'application/vnd.google-apps.folder' and name = '" + item.Name + "'";
                        FileList result = request.Execute();
                        File matchingGoogleFolder = null;
                        if (result.Files.Count == 0)
                        {
                            // Not exist, create one
                            File folderMetadata = new File
                            {
                                Name = item.Name,
                                MimeType = "application/vnd.google-apps.folder",
                                Parents = new List<string> { googleFolderId }
                            };
                            FilesResource.CreateRequest requestUpload = googleDrive.Files.Create(folderMetadata);
                            matchingGoogleFolder = await requestUpload.ExecuteAsync();
                        }
                        else
                        {
                            matchingGoogleFolder = result.Files[0];
                        }
                        // Lets go deeper
                        await SyncGoogleDriveAsync(item as StorageFolder, matchingGoogleFolder.Id, googleDrive);
                    }
                }
                else
                {
                    // If folder is build, check if zip exist in Google Drive
                    FilesResource.ListRequest request = googleDrive.Files.List();
                    request.Q = "'" + googleFolderId + "' in parents and name = '" + item.Name + "'";
                    FileList result = request.Execute();
                    if (result.Files.Count == 0)
                    {
                        // Not exist, upload to Google Drive
                        StorageFile tempFile = await (item as StorageFile).CopyAsync(ApplicationData.Current.TemporaryFolder, item.Name, NameCollisionOption.ReplaceExisting);

                        File fileMetadata = new File();
                        fileMetadata.Name = tempFile.Name;
                        fileMetadata.MimeType = tempFile.ContentType;
                        fileMetadata.CreatedTime = item.DateCreated.DateTime;
                        fileMetadata.Parents = new List<string> { googleFolderId };
                        FilesResource.CreateMediaUpload requestUpload;

                        
                        
                        using (System.IO.FileStream stream = new System.IO.FileStream(tempFile.Path, System.IO.FileMode.Open))
                        {
                            await CreateUploadRequest(googleDrive, stream, tempFile.Name, tempFile.ContentType);
                            requestUpload = googleDrive.Files.Create(fileMetadata, stream, tempFile.ContentType);
                            requestUpload.Fields = "id";
                            requestUpload.ProgressChanged += videosInsertRequest_ProgressChanged;
                            requestUpload.ResponseReceived += videosInsertRequest_ResponseReceived;
                            requestUpload.ChunkSize = 256 * 1024;  //default size is 10 I think
                            await requestUpload.UploadAsync();
                            File uploadedFile = requestUpload.ResponseBody;
                        }
                    }

                    // If build is too old, delete it.
                    TimeSpan timeDiff = DateTime.Now.Subtract(item.DateCreated.DateTime);
                }
            }
        }

        static void videosInsertRequest_ProgressChanged(IUploadProgress progress)
        {
            switch (progress.Status)
            {
                case UploadStatus.Uploading:
                    
                    break;

                case UploadStatus.Failed:
                    // log_writeline("An error prevented the upload from completing.\n" + progress.Exception.ToString());
                    break;
            }

            Debug.WriteLine("{0}: {1} bytes sent.", progress.Status.ToString(), progress.BytesSent);
        }

        static void videosInsertRequest_ResponseReceived(File video)
        {
            // log_writeline("Video " + video.Snippet.Title + " was successfully uploaded.");
        }

        public static async Task<HttpWebRequest> CreateUploadRequest(DriveService driveService, System.IO.FileStream contentStream, string title, string mimeType, string description = null)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable");

            request.Method = "POST";

            Dictionary<string, object> requestBody = new Dictionary<string, object>();
            requestBody["title"] = title;
            requestBody["mimeType"] = mimeType;

            if (!string.IsNullOrWhiteSpace(description))
            {
                requestBody["description"] = description;
            }

            //driveService.Authenticator.ApplyAuthenticationToRequest(request);


            System.IO.Stream requestStream = await request.GetRequestStreamAsync();

            ////How to do that???
            //string formdataTemplate = "Content-Disposition: form-data; name=\"{0}\"\r\n\r\n{1}";
            //foreach (string key in nvc.Keys)
            //{
            //    rs.Write(boundarybytes, 0, boundarybytes.Length);
            //    string formitem = string.Format(formdataTemplate, key, nvc[key]);
            //    byte[] formitembytes = System.Text.Encoding.UTF8.GetBytes(formitem);
            //    rs.Write(formitembytes, 0, formitembytes.Length);
            //}
            //rs.Write(boundarybytes, 0, boundarybytes.Length);

            //string headerTemplate = "Content-Disposition: form-data; name=\"{0}\"; filename=\"{1}\"\r\nContent-Type: {2}\r\n\r\n";
            //string header = string.Format(headerTemplate, paramName, file, contentType);
            //byte[] headerbytes = System.Text.Encoding.UTF8.GetBytes(header);
            //rs.Write(headerbytes, 0, headerbytes.Length);

            //FileStream fileStream = new FileStream(file, FileMode.Open, FileAccess.Read);
            //byte[] buffer = new byte[4096];
            //int bytesRead = 0;
            //while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) != 0)
            //{
            //    rs.Write(buffer, 0, bytesRead);
            //}
            //fileStream.Close();

            //byte[] trailer = System.Text.Encoding.ASCII.GetBytes("\r\n--" + boundary + "--\r\n");
            //rs.Write(trailer, 0, trailer.Length);
            //rs.Close();

            //requestStream.Close();

            return request;
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
    }
}
