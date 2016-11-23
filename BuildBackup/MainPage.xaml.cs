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
using Windows.Storage.Search;
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
        public enum TraceLevel
        {
            Off = 0,
            Error,
            Warning,
            Info,
            Verbose
        }
        public enum SortOrder
        {
            NameAsc,
            NameDesc,
            DateCreatedAsc,
            DateCreatedDesc,
            DateModifiedAsc,
            DateModifiedDesc
        }

        static private TraceLevel _traceLevel = TraceLevel.Verbose;
        static private bool _cancel = false;
        static private string _log;

        static private int totalFoundItem;
        static private int proceedFoundItem;

        ThreadPoolTimer m_timerFolderCheck = null;
        IReadOnlyList<StorageFolder> m_folders;

        DriveService m_driveService = null;
        string m_buildBackupFolderId;
        private Payload p;

        public MainPage()
        {
            this.InitializeComponent();

            p = new Payload();

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

            // Get all folders from Google Drive's root
            FileList result = GoogleDriveItems(m_driveService, "root", SortOrder.NameAsc);
            foreach (File item in result.Files)
            {
                if (item.MimeType != "application/vnd.google-apps.folder")
                    continue;

                ComboBoxItem cbItem = new ComboBoxItem { Content = item.Name, Name = item.Id, Tag = item.Id };
                comboBoxGoogleFolders.Items.Add(cbItem);

                // Initially selected Build_Backup
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

            StorageFile logFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt");

            await SyncGoogleDriveAsync(rootFolder, m_buildBackupFolderId, m_driveService);
            totalFoundItem = proceedFoundItem = 0;
            p.ProcessingItemProgress = p.FoundItemProgress = 0;

            if (logFile != null)
            {
                Utility.UIThreadExecute(async () =>
                {
                    await FileIO.WriteTextAsync(logFile, p.Log);
                });
            }

            // Check again after 6 hours
            m_timerFolderCheck = ThreadPoolTimer.CreateTimer((newTimer) => FolderCheckTimerElpasedHandler(newTimer, rootFolder), TimeSpan.FromHours(6));
        }

        public async Task SyncGoogleDriveAsync(StorageFolder localFolder, string googleFolderId, DriveService googleDrive)
        {
            try
            {
                IReadOnlyList<IStorageItem> localItems = await SortStorageItemsAsync(localFolder, SortOrder.DateModifiedAsc);
                totalFoundItem += localItems.Count;
                // If folder contains nothing, delete itself
                //if (localItems.Count <= 0 && !localFolder.Name.EndsWith("_BUILDS"))
                //{
                //    await localFolder.DeleteAsync();
                //    UpdateStatus(TraceLevel.Warning, string.Format("Deleted\t{0}", localFolder.Path));
                //    return;
                //}

                FileList googleItems = GoogleDriveItems(googleDrive, googleFolderId, SortOrder.NameAsc);
                foreach (IStorageItem item in localItems)
                {
                    if (_cancel)
                    {
                        UpdateStatus(TraceLevel.Info, "Backup cancelled");
                        break;
                    }

                    UpdateStatus(TraceLevel.Verbose, item.Path);
                    p.FoundItemProgress = (double)++proceedFoundItem / totalFoundItem * 100;
                    p.ProcessingItemProgress = 0;

                    if (item.IsOfType(StorageItemTypes.Folder))
                    {
                        // If folder is build, but only contains 1 file (e.g. ISO, ZIP, EXE), simply go deeper
                        IStorageItem setupFile = await (item as StorageFolder).TryGetItemAsync("setup.exe");
                        IStorageItem autorunFile = await (item as StorageFolder).TryGetItemAsync("autorun.exe");
                        IStorageItem issetupFile = await (item as StorageFolder).TryGetItemAsync("issetup.dll");

                        //if (item.Name.Contains("_LOGID") && !(folderItems.Count == 1 && folderItems[0].IsOfType(StorageItemTypes.File)))
                        if (setupFile != null || autorunFile != null || issetupFile != null)
                        {
                            UpdateStatus(TraceLevel.Info, string.Format("Folder contains build\t{0}", item.Path));
                            // If folder is build, check if zip exist in Google Drive
                            File uploadedFile = GoogleDriveIsItemExist(googleItems, item.Name + ".zip", "application/x-zip-compressed");
                            // Check again if the entire folder has been uploaded.
                            if (uploadedFile == null)
                                uploadedFile = GoogleDriveIsItemExist(googleItems, item.Name, "application/vnd.google-apps.folder");

                            if (uploadedFile == null)
                            {
                                // Not exist, zip and upload
                                // Workaround: http://stackoverflow.com/questions/33801760/
                                string zipName = DateTime.Now.ToString("HHmmssfff");
                                string zipPath = string.Format("{0}\\{1}.zip", ApplicationData.Current.TemporaryFolder.Path, zipName);
                                UpdateStatus(TraceLevel.Info, string.Format("Zipping\t{0}", zipPath));
                                await CopyFolderAsync(item as StorageFolder, ApplicationData.Current.TemporaryFolder, zipName);
                                StorageFolder tempFolder = await ApplicationData.Current.TemporaryFolder.GetFolderAsync(zipName);
                                ZipFile.CreateFromDirectory(tempFolder.Path, zipPath, CompressionLevel.Optimal, false);
                                StorageFile tempFile = await ApplicationData.Current.TemporaryFolder.GetFileAsync(zipName + ".zip");
                                UpdateStatus(TraceLevel.Info, string.Format("Zipped\t{0}", zipPath));

                                // Upload to Google Drive
                                uploadedFile = await UploadAsync(googleDrive, new List<string> { googleFolderId }, tempFile, item.Name + ".zip");
                                UpdateStatus(TraceLevel.Warning, string.Format("Uploaded\t{0} to https://drive.google.com/open?id={1}", zipPath, uploadedFile.Id));

                                // Delete temp file and folder
                                await tempFolder.DeleteAsync(StorageDeleteOption.PermanentDelete);
                                await tempFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                            }

                            if (uploadedFile != null && p.CanDeleteOldFiles == true)
                            {
                                // If build is too old (180days), delete it.
                                if (DateTime.Now.Subtract(item.DateCreated.DateTime).Days > p.DaysToDelete)
                                {
                                    await item.DeleteAsync(StorageDeleteOption.PermanentDelete);
                                    UpdateStatus(TraceLevel.Warning, string.Format("Deleted\t{0} created on {1}", item.Path, item.DateCreated.Date));
                                }
                            }
                        }
                        else
                        {
                            // Check if folder exist in Google Drive
                            File matchingGoogleFolder = GoogleDriveIsItemExist(googleItems, item.Name, "application/vnd.google-apps.folder");
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
                        // If item is file, check if exist in Google Drive.
                        File uploadedFile = GoogleDriveIsItemExist(googleItems, item.Name, (item as StorageFile).ContentType);
                        if (uploadedFile == null)
                        {
                            // Not exist, upload to Google Drive
                            UpdateStatus(TraceLevel.Info, string.Format("Preparing\t{0}", item.Path));
                            StorageFile tempFile = await (item as StorageFile).CopyAsync(ApplicationData.Current.TemporaryFolder, item.Name, NameCollisionOption.ReplaceExisting);
                            uploadedFile = await UploadAsync(googleDrive, new List<string> { googleFolderId }, tempFile);
                            UpdateStatus(TraceLevel.Warning, string.Format("Uploaded\t{0}", item.Path));

                            await tempFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                        }

                        if (uploadedFile != null && p.CanDeleteOldFiles == true)
                        {
                            // If build is too large (50MB) and too old (180days), delete it.
                            BasicProperties prop = await (item as StorageFile).GetBasicPropertiesAsync();
                            if (prop.Size > 50 * 1048576 && DateTime.Now.Subtract(item.DateCreated.DateTime).Days > p.DaysToDelete)
                            {
                                await item.DeleteAsync(StorageDeleteOption.PermanentDelete);
                                UpdateStatus(TraceLevel.Warning, string.Format("Deleted\t{0} created on {1} size {2:N}MB", item.Path, item.DateCreated.Date, (double)prop.Size / 1048576));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                UpdateStatus(TraceLevel.Error, string.Format("Sync failed\t{0} message {1}", localFolder.Path, ex.Message));
            }
        }

        private void SaveStatusToLog()
        {

        }

        private void UpdateStatus(TraceLevel level, string msg)
        {
            if (level == TraceLevel.Verbose)
                p.FoundItem = msg;
            else
                p.ProcessingItem = string.Format("[{0}] {1}", level.ToString().PadLeft(7), msg);

            Debug.WriteLine(string.Format("[{0}][{1}] {2}\n", level.ToString().PadLeft(7), DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss.fff"), msg));
        }

        public async Task<File> GoogleDriveCreateFolderAsync(DriveService driveService, string parent, string itemName)
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
                UpdateStatus(TraceLevel.Error, string.Format("Folder create failed\t{0} parent {1} message {2}", itemName, parent, ex.Message));
            }
            UpdateStatus(TraceLevel.Info, string.Format("Folder created\t{0} at {1}", itemName, googleFolder.Id));
            return googleFolder;
        }

        public FileList GoogleDriveItems(DriveService driveService, string parent, SortOrder order)
        {
            FileList sortedResult = new FileList();
            try
            {
                string nextPageToken = null;
                IEnumerable<File> files = new List<File>();
                do
                {
                    FilesResource.ListRequest request = driveService.Files.List();
                    request.Q = "'" + parent + "' in parents";
                    request.Q += " and trashed = false";
                    request.Spaces = "drive";
                    request.PageToken = nextPageToken;
                    FileList result = request.Execute();
                    files = files.Concat(result.Files);
                } while (nextPageToken != null);

                switch (order)
                {
                    case SortOrder.NameAsc:
                        sortedResult.Files = files.OrderBy(x => x.Name).ToList();
                        break;
                    case SortOrder.NameDesc:
                        sortedResult.Files = files.OrderByDescending(x => x.Name).ToList();
                        break;
                    case SortOrder.DateCreatedAsc:
                        sortedResult.Files = files.OrderBy(x => x.CreatedTime.GetValueOrDefault()).ToList();
                        break;
                    case SortOrder.DateCreatedDesc:
                        sortedResult.Files = files.OrderByDescending(x => x.CreatedTime.GetValueOrDefault()).ToList();
                        break;
                    case SortOrder.DateModifiedAsc:
                        sortedResult.Files = files.OrderBy(x => x.ModifiedTime.GetValueOrDefault()).ToList();
                        break;
                    case SortOrder.DateModifiedDesc:
                        sortedResult.Files = files.OrderByDescending(x => x.ModifiedTime.GetValueOrDefault()).ToList();
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                UpdateStatus(TraceLevel.Error, string.Format("Folder list failed\t{0} message {1}", parent, ex.Message));
            }

            return sortedResult;
        }

        public File GoogleDriveIsItemExist(FileList googleItems, string itemName, string contentType)
        {
            File googleItem = null;
            foreach (File item in googleItems.Files)
            {
                if (itemName == item.Name)
                {
                    googleItem = item;
                    break;
                }
            }

            return googleItem;
        }

        public File GoogleDriveIsItemExist(DriveService driveService, string parent, string itemName, string contentType)
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
                request.Q = "name = '" + itemName.Replace("'", "\\'") + "'";
                request.Q += " and '" + parent + "' in parents";
                request.Q += string.IsNullOrWhiteSpace(contentType) ? "" : " and mimeType = '" + contentType + "'";
                request.Q += " and trashed = false";
                FileList result = request.Execute();
                if (result.Files.Count == 1)
                    googleItem = result.Files[0];
                else if (result.Files.Count > 1)
                    throw new Exception(string.Format("Found {0} files", result.Files.Count));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                UpdateStatus(TraceLevel.Error, string.Format("File query failed\t{0} message {1}", itemName , ex.Message));
            }
            return googleItem;
        }

        public async Task<File> UploadAsync(DriveService driveService, IList<string> parents, StorageFile file, string itemName = "")
        {
            if (string.IsNullOrWhiteSpace(itemName))
                itemName = file.Name;

            // Prepare the JSON metadata
            string json = "{\"name\":\"" + itemName.Replace("'", "\\'") + "\"";
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
                    throw new Exception(string.Format("Upload request failed: {0} {1}", httpResponse.StatusCode, httpResponse.StatusDescription));

                // Step 3: Upload the file in chunks
                using (System.IO.FileStream fileStream = new System.IO.FileStream(file.Path, System.IO.FileMode.Open))
                {
                    string uploadID = httpResponse.Headers["x-guploader-uploadid"];
                    ulong chunkSize = 100 * 1024 * 1024; // 100MB
                    ulong transferedSize = 0;
                    byte[] buffer = new byte[chunkSize];

                    while (transferedSize < fileSize)
                    {
                        ulong writeSize = Math.Min(chunkSize, fileSize - transferedSize);
                        httpRequest = (HttpWebRequest)WebRequest.Create("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&upload_id=" + uploadID);
                        httpRequest.Headers["Content-Type"] = file.ContentType;
                        httpRequest.Headers["Content-Length"] = writeSize.ToString();
                        httpRequest.Headers["Content-Range"] = string.Format("bytes {0}-{1}/{2}", transferedSize, Math.Min(chunkSize - 1 + transferedSize, fileSize - 1), fileSize);
                        httpRequest.Method = "PUT";

                        using (System.IO.Stream requestStream = await httpRequest.GetRequestStreamAsync())
                        {
                            fileStream.Read(buffer, 0, (int)writeSize);
                            requestStream.Write(buffer, 0, (int)writeSize);
                        }

                        // Response is throw as exception when upload in chunks
                        try
                        {
                            httpResponse = (HttpWebResponse)await httpRequest.GetResponseAsync();
                        }
                        catch (WebException wex)
                        {
                            httpResponse = (HttpWebResponse)wex.Response;
                            if ((int)httpResponse.StatusCode == 308)
                            {
                                string range = httpResponse.Headers["Range"];
                                transferedSize = Convert.ToUInt64(range.Substring(range.LastIndexOf("-") + 1)) + 1;
                                p.ProcessingItemProgress = (double)transferedSize / fileSize * 100;
                                Debug.WriteLine(string.Format("Uploaded {0} bytes", transferedSize));
                                continue;
                            }
                            else
                            {
                                using (System.IO.Stream responseStream = httpResponse.GetResponseStream())
                                using (System.IO.StreamReader reader = new System.IO.StreamReader(responseStream, System.Text.Encoding.UTF8))
                                {
                                    throw new Exception(string.Format("Upload file failed: {0} {1}", httpResponse.StatusCode, reader.ReadToEnd()));
                                }
                            }
                        }

                        if (httpResponse.StatusCode == HttpStatusCode.OK)
                            break;
                        else
                            throw new Exception(string.Format("Upload request failed: {0} {1}", httpResponse.StatusCode, httpResponse.StatusDescription));
                    }
                }

                // Try to retrieve the file from Google
                FilesResource.ListRequest request = driveService.Files.List();
                if (parents.Count > 0)
                    request.Q += "'" + parents[0] + "' in parents and ";
                request.Q += "name = '" + itemName.Replace("'", "\\'") + "'";
                FileList result = request.Execute();
                if (result.Files.Count > 0)
                    uploadedFile = result.Files[0];
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                UpdateStatus(TraceLevel.Error, string.Format("File upload failed\t{0} message {1}", file.Path, ex.Message));
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

        private async Task<IReadOnlyList<IStorageItem>> SortStorageItemsAsync(StorageFolder localFolder, SortOrder order)
        {            
            IReadOnlyList<IStorageItem> localItems = null;
            try
            {
                // initialize queryOptions using a common query
                QueryOptions queryOptions = new QueryOptions();

                // clear all existing sorts
                queryOptions.SortOrder.Clear();

                // add descending sort by date modified
                SortEntry se = new SortEntry();
                switch (order)
                {
                    case SortOrder.NameAsc:
                        se.PropertyName = "System.FileName";
                        se.AscendingOrder = true;
                        break;
                    case SortOrder.NameDesc:
                        se.PropertyName = "System.FileName";
                        se.AscendingOrder = false;
                        break;
                    case SortOrder.DateCreatedAsc:
                        se.PropertyName = "System.DateCreated";
                        se.AscendingOrder = true;
                        break;
                    case SortOrder.DateCreatedDesc:
                        se.PropertyName = "System.DateCreated";
                        se.AscendingOrder = false;
                        break;
                    case SortOrder.DateModifiedAsc:
                        se.PropertyName = "System.DateModified";
                        se.AscendingOrder = true;
                        break;
                    case SortOrder.DateModifiedDesc:
                        se.PropertyName = "System.DateModified";
                        se.AscendingOrder = false;
                        break;
                }
                queryOptions.SortOrder.Add(se);

                StorageItemQueryResult queryResult = localFolder.CreateItemQueryWithOptions(queryOptions);
                localItems = await queryResult.GetItemsAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                UpdateStatus(TraceLevel.Error, string.Format("Failed to sort files in folder: {0} error: {1}", localFolder, ex.Message));
            }

            return localItems;
        }


        private void comboBoxGoogleFolders_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            m_buildBackupFolderId = (comboBoxGoogleFolders.SelectedItem as ComboBoxItem).Tag.ToString();
        }

        private async void buttonStartBackup_Click(object sender, RoutedEventArgs e)
        {
            _cancel = false;
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

        private void buttonCancelBackup_Click(object sender, RoutedEventArgs e)
        {
            _cancel = true;
        }
    }
}
