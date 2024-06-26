﻿using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;
using Google.Apis.Services;
using Google.Apis.Upload;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
//using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.Storage.Search;
using Windows.Storage.Streams;
using Windows.System.Threading;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

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

        public struct ParseFolderParams
        {
            public DriveService GoogleDrive;
            public KeyValuePair<File, StorageFolder> LocalFolder;
            public FileList GoogleItems;
            public Func<IStorageItem, Task<bool>> ValidateItemFunc;
            public Func<IStorageItem, bool> IgnoreItemFunc;
            /// <summary>
            /// Prepare storageItem either zip or unzip to send to Google drive
            /// </summary>
            public Func<string, DriveService, FileList, IStorageItem, Payload, Task<SyncItem>> PrepareItemFunc;
            /// <summary>
            /// Determine storageItem belongs to which folder on Google drive
            /// </summary>
            public Func<DriveService, File, FileList, IStorageItem, Payload, Task<File>> SelectGoogleFolderFunc;
            public Func<List<SyncItem>, List<SyncItem>> KeepFolderFunc;
            public IProgress<long> Progress;
            public CancellationToken CancelToken;
        }

        static private TraceLevel _traceLevel = TraceLevel.Verbose;
        static private string _log;
        static private ApplicationDataContainer _localSettings = ApplicationData.Current.LocalSettings;

        static private int totalFoundItem;
        static private int proceedFoundItem;

        ThreadPoolTimer m_timerWatchNewLog = null;
        ThreadPoolTimer m_timerFolderCheck = null;
        Dictionary<string, StorageFolder> m_backupFolders = new Dictionary<string, StorageFolder>();
        CancellationTokenSource m_cts = null;

        DriveService m_driveService = null;
        private Payload p;
        private ObservableCollection<SyncItem> listSyncItems = new ObservableCollection<SyncItem>();


        public MainPage()
        {
            this.InitializeComponent();

            p = new Payload();
            listViewStatus.ItemsSource = listSyncItems;
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

            object objLogID = _localSettings.Values["LastLogID"];
            int latestLogID = 0;
            int.TryParse(objLogID?.ToString(), out latestLogID);
            p.LatestLogID = latestLogID;
            m_cts = new CancellationTokenSource();
            m_timerWatchNewLog = ThreadPoolTimer.CreateTimer((newTimer) => WatchNewLogTimerElpasedHandler(newTimer, m_backupFolders, p.LatestLogID), TimeSpan.FromSeconds(p.LoopInterval));
        }

        class FileComparer : IComparer<File>
        {
            public int Compare(File x, File y)
            {
                if (x != null)
                    return 1;
                else
                    return -1;
            }
        }

        private async void FolderSyncTimerElpasedHandler(ThreadPoolTimer timer, Dictionary<string, StorageFolder> backupFolders, int latestLogID)
        {
            if (timer != null)
            {
                timer.Cancel();
                timer = null;
            }

            StorageFile logFile = await p.TempFolder.CreateFileAsync(DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt");
            Utility.UIThreadExecute(() =>
            {
                listSyncItems.Clear();
            });
            foreach (KeyValuePair<string, StorageFolder> backupFolder in backupFolders)
            {
                ParseFolderParams pms;
                if (backupFolder.Key == "0B8j6UJY_E28CdGg5bXV2dGQtZzQ")
                {
                    pms = new ParseFolderParams
                    {
                        GoogleDrive = m_driveService,
                        LocalFolder = new KeyValuePair<File, StorageFolder>(await GoogleDriveCreateFolderAsync(m_driveService, "root", "BuildLogs_Backup"), backupFolder.Value),
                        GoogleItems = GoogleDriveItems(m_driveService, backupFolder.Key, SortOrder.NameAsc),  // Get all items from matching Google folder.
                        ValidateItemFunc = IsValidLogItemAsync,
                        IgnoreItemFunc = IsIgnoredLogItemAsync,
                        PrepareItemFunc = PrepareLogItemAsync,
                        SelectGoogleFolderFunc = SelectLogGoogleFolderAsync,
                        KeepFolderFunc = IsKeepLogItem,
                        Progress = new Progress<long>(percent => { Debug.WriteLine(string.Format("Log {0}", percent)); }),
                        CancelToken = m_cts.Token
                    };

                    IReadOnlyList<SyncItem> list = await ParseFoldersAsync(pms);
                    SyncItemsAsync(list, m_driveService, pms.Progress, pms.CancelToken);
                }
                else if (backupFolder.Key == "0B8j6UJY_E28CfkpjUnJ0NGUxcFZmVHVTNkhXZFg2TmF1REpPc2E4WEQ4OVBsZVc1V1RlQjg")
                {
                    pms = new ParseFolderParams
                    {
                        GoogleDrive = m_driveService,
                        LocalFolder = new KeyValuePair<File, StorageFolder>(await GoogleDriveCreateFolderAsync(m_driveService, "root", "Build_Backup"), backupFolder.Value),
                        GoogleItems = GoogleDriveItems(m_driveService, backupFolder.Key, SortOrder.NameAsc),  // Get all items from matching Google folder.
                        ValidateItemFunc = IsValidBuildItemAsync,
                        IgnoreItemFunc = IsIgnoredBuildItemAsync,
                        PrepareItemFunc = PrepareBuildItemAsync,
                        SelectGoogleFolderFunc = SelectBuildGoogleFolderAsync,
                        KeepFolderFunc = IsKeepBuildItem,
                        Progress = new Progress<long>(percent => { Debug.WriteLine(string.Format("Build {0}", percent)); }),
                        CancelToken = m_cts.Token
                    };

                    IReadOnlyList<SyncItem> list = await ParseFoldersAsync(pms);
                    SyncItemsAsync(list, m_driveService, pms.Progress, pms.CancelToken);
                }
                else if (backupFolder.Key == "0B8j6UJY_E28Cfk9GNWlxVDF4NGkxOGFmNFpsQlJIV0JFVWxMUVFIX3F2bXVrOW9ldmVxU28")
                {
                    pms = new ParseFolderParams
                    {
                        GoogleDrive = m_driveService,
                        LocalFolder = new KeyValuePair<File, StorageFolder>(await GoogleDriveCreateFolderAsync(m_driveService, "0B8j6UJY_E28CfkpjUnJ0NGUxcFZmVHVTNkhXZFg2TmF1REpPc2E4WEQ4OVBsZVc1V1RlQjg", "_BinaryDepot"), backupFolder.Value),
                        GoogleItems = GoogleDriveItems(m_driveService, backupFolder.Key, SortOrder.NameAsc),  // Get all items from matching Google folder.
                        ValidateItemFunc = IsValidBinaryDepotItemAsync,
                        IgnoreItemFunc = IsIgnoredBinaryDepotItemAsync,
                        PrepareItemFunc = PrepareBinaryDepotItemAsync,
                        SelectGoogleFolderFunc = SelectBinaryDepotGoogleFolderAsync,
                        KeepFolderFunc = IsKeepBinaryDepotItem,
                        Progress = new Progress<long>(percent => { Debug.WriteLine(string.Format("Binary Depot {0}", percent)); }),
                        CancelToken = m_cts.Token
                    };

                    IReadOnlyList<SyncItem> list = await ParseFoldersAsync(pms);
                    SyncItemsAsync(list, m_driveService, pms.Progress, pms.CancelToken);
                }
                else if (backupFolder.Key == "0B8j6UJY_E28CWnJ2MEFqYkgxbGc")
                {
                    pms = new ParseFolderParams
                    {
                        GoogleDrive = m_driveService,
                        LocalFolder = new KeyValuePair<File, StorageFolder>(await GoogleDriveCreateFolderAsync(m_driveService, "root", "ComponentSDK_Backup"), backupFolder.Value),
                        GoogleItems = GoogleDriveItems(m_driveService, backupFolder.Key, SortOrder.NameAsc),  // Get all items from matching Google folder.
                        ValidateItemFunc = IsValidComponentSDKItemAsync,
                        IgnoreItemFunc = IsIgnoredComponentSDKItem,
                        PrepareItemFunc = PrepareComponentSDKItemAsync,
                        SelectGoogleFolderFunc = SelectComponentSDKGoogleFolderAsync,
                        KeepFolderFunc = IsKeepComponentSDKFolderAsync,
                        Progress = new Progress<long>(percent => { Debug.WriteLine(string.Format("ComponentSDK {0}", percent)); }),
                        CancelToken = m_cts.Token
                    };

                    IReadOnlyList<SyncItem> list = await ParseFoldersAsync(pms);
                    SyncItemsAsync(list, m_driveService, pms.Progress, pms.CancelToken);
                }
            }
            totalFoundItem = proceedFoundItem = 0;
            p.ProcessingItemProgress = p.FoundItemProgress = 0;

            p.LatestLogID = latestLogID;
            p.LastUpdateTime = DateTime.Now.ToString();

            if (logFile != null)
            {
                Utility.UIThreadExecute(async () =>
                {
                    await FileIO.WriteTextAsync(logFile, p.Log);
                });
            }

            // Continue to watch new log
            m_timerWatchNewLog = ThreadPoolTimer.CreateTimer((newTimer) => WatchNewLogTimerElpasedHandler(newTimer, backupFolders, p.LatestLogID), TimeSpan.FromSeconds(p.LoopInterval));
        }
        
        /// <summary>
        /// A thread that simply delete an IStorageItem, to avoid blocking other tasks.
        /// </summary>
        /// <param name="timer"></param>
        /// <param name="storageItem"></param>
        private async void StorageItemDeleteTimerElpasedHandler(ThreadPoolTimer timer, IStorageItem storageItem)
        {
            if (timer != null)
            {
                timer.Cancel();
                timer = null;
            }

            try
            {
                await storageItem.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                UpdateStatus(TraceLevel.Error, string.Format("Delete failed\t{0} message {1}", storageItem.Path, ex.Message));
            }
        }

        /// <summary>
        /// Watch the log folder if new log file is generated.
        /// </summary>
        /// <param name="timer"></param>
        /// <param name="backupFolders"></param>
        /// <param name="lastLogID"></param>
        private async void WatchNewLogTimerElpasedHandler(ThreadPoolTimer timer, Dictionary<string, StorageFolder> backupFolders, int latestLogID)
        {
            if (timer != null)
            {
                timer.Cancel();
                timer = null;
            }

            StorageFolder vFolder;
            int curLogID = 0;
            StorageFile newFile = null;
            if (backupFolders.ContainsKey("0B8j6UJY_E28CdGg5bXV2dGQtZzQ") &&    // Build log
                backupFolders.ContainsKey("0B8j6UJY_E28CfkpjUnJ0NGUxcFZmVHVTNkhXZFg2TmF1REpPc2E4WEQ4OVBsZVc1V1RlQjg") &&    // Builds
                backupFolders.ContainsKey("0B8j6UJY_E28Cfk9GNWlxVDF4NGkxOGFmNFpsQlJIV0JFVWxMUVFIX3F2bXVrOW9ldmVxU28") &&    // Binary depot
                backupFolders.ContainsKey("0B8j6UJY_E28CWnJ2MEFqYkgxbGc") &&    // ComponentSDK
                p.TempFolder != null)
            {
                m_backupFolders.TryGetValue("0B8j6UJY_E28CdGg5bXV2dGQtZzQ", out vFolder);
                StorageFolder logFolder = await vFolder.GetFolderAsync("log");
                IReadOnlyList<StorageFile> files = await logFolder?.GetFilesAsync();
                foreach (StorageFile file in files)
                {
                    if (file.FileType != ".log")
                        continue;
                    
                    if (newFile == null || file.DateCreated > newFile.DateCreated)
                    {
                        newFile = file;
                    }                    
                }

            }
            int.TryParse(newFile?.DisplayName, out curLogID);

            if (curLogID != p.LatestLogID)
                m_timerFolderCheck = ThreadPoolTimer.CreateTimer((folderTimer) => FolderSyncTimerElpasedHandler(folderTimer, m_backupFolders, curLogID), TimeSpan.FromMilliseconds(10));
            else
                m_timerWatchNewLog = ThreadPoolTimer.CreateTimer((newTimer) => WatchNewLogTimerElpasedHandler(newTimer, backupFolders, latestLogID), TimeSpan.FromSeconds(p.LoopInterval));
        }

        public async Task SyncBuildLogsAsync(StorageFolder localFolder, string googleFolderId, DriveService googleDrive, IProgress<long> progress, CancellationToken cancelToken)
        {
            try
            {
                FileList googleItems = GoogleDriveItems(googleDrive, googleFolderId, SortOrder.NameAsc);
                foreach (File googlelogFolder in googleItems.Files)
                {
                    //if (googlelogFolder.Name != "p4log")
                    //    continue;
                    // First we find the matching local log folder
                    StorageFolder localLogFolder = await localFolder.GetFolderAsync(googlelogFolder.Name);
                    // Proceed with subfolders first
                    IReadOnlyList<IStorageFolder> localSubFolders = await GetAllSubfoldersAsync(localLogFolder);

                    foreach (StorageFolder localSubFolder in localSubFolders)
                    {
                        IReadOnlyList<StorageFile> files = await localSubFolder.GetFilesAsync();
                        totalFoundItem += files.Count;
                        File googleSubfolder = null;
                        FileList googleSubfolderItems = null;
                        List<StorageFile> filesToDelete = new List<StorageFile>();
                        foreach (StorageFile file in files)
                        {
                            /////
                            SyncItem syncItem = new SyncItem();
                            Utility.UIThreadExecute(() =>
                            {
                                listSyncItems.Add(syncItem);
                            });
                            //syncItem.BackupSource = file.Path;
                            /////

                            if (cancelToken.IsCancellationRequested)
                            {
                                UpdateStatus(TraceLevel.Info, "Backup cancelled");
                                cancelToken.ThrowIfCancellationRequested();
                                break;
                            }
                            UpdateStatus(TraceLevel.Verbose, file.Path);
                            p.FoundItemProgress = (double)++proceedFoundItem / totalFoundItem * 100;
                            p.ProcessingItemProgress = 0;

                            string googleFilename = file.Name;
                            if (file.FileType == ".zip")
                            {
                                googleFilename = file.DisplayName;
                            }

                            // Change extension to ".log" for easy access.
                            string ext = System.IO.Path.GetExtension(googleFilename);
                            if (ext != ".log" && ext != ".txt")
                                googleFilename = System.IO.Path.ChangeExtension(googleFilename, ".log");

                            string matchingFolderName = GetLogSubfolderName(file.Name, p.FolderMaxItems);
                            if (googleSubfolder == null || googleSubfolder.Name != matchingFolderName)
                            {
                                googleSubfolder = await GoogleDriveCreateFolderAsync(googleDrive, googlelogFolder.Id, matchingFolderName);
                                googleSubfolderItems = GoogleDriveItems(googleDrive, googleSubfolder.Id, SortOrder.NameAsc);
                                UpdateStatus(TraceLevel.Info, string.Format("Get {0} items from associated Google Drive folder\t{1}", googleSubfolderItems.Files.Count, googleSubfolder.Name));
                            }

                            File uploadedFile = GoogleDriveIsItemExist(googleSubfolderItems, googleFilename, "text/plain");
                            if (uploadedFile == null)
                            {
                                StorageFile tempZipFile = null;
                                StorageFile tempFile = null;

                                if (file.FileType == ".zip")
                                {
                                    // Unzip to retrieve the log file, it will be easier to view and search on Google Drive
                                    UpdateStatus(TraceLevel.Info, string.Format("Unzipping\t{0}", file.Name));
                                    tempZipFile = await file.CopyAsync(p.TempFolder, file.Name, NameCollisionOption.ReplaceExisting);
                                    ZipFile.ExtractToDirectory(tempZipFile.Path, p.TempFolder.Path);
                                    tempFile = await p.TempFolder.GetFileAsync(file.DisplayName);
                                }
                                else if (file.FileType == ".gz")
                                {
                                    // ZipFile can't handle gz, skip
                                    UpdateStatus(TraceLevel.Info, string.Format("Can't unzip .gz files\t{0}", file.Name));
                                    continue;
                                }
                                else
                                {
                                    UpdateStatus(TraceLevel.Info, string.Format("Raw text log\t{0}", file.Name));
                                    tempFile = await file.CopyAsync(p.TempFolder, file.Name, NameCollisionOption.ReplaceExisting);
                                }

                                // Upload file to Google Drive
                                uploadedFile = await UploadAsync(googleDrive, new List<string> { googleSubfolder.Id }, tempFile, googleFilename, new Progress<long>(percent => { Debug.WriteLine(string.Format("Uploaded {0}", percent)); }), m_cts.Token);
                                if (uploadedFile != null)
                                {
                                    UpdateStatus(TraceLevel.Warning, string.Format("Uploaded\t{0} to https://drive.google.com/open?id={1}", tempFile.Path, uploadedFile.Id));
                                    //syncItem.UploadDestination = string.Format("https://drive.google.com/open?id={0}", uploadedFile.Id);
                                }
                                else
                                    UpdateStatus(TraceLevel.Error, string.Format("Upload failed\t{0}", tempFile.Path));

                                try
                                {
                                    // Delete temp file and folder
                                    if (tempFile != null)
                                        await tempFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                                    if (tempZipFile != null)
                                        await tempZipFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                                }
                                catch (System.IO.FileNotFoundException ioex)
                                {
                                    Debug.WriteLine(ioex.Message);
                                    UpdateStatus(TraceLevel.Warning, string.Format("Delete temp failed\t{0} message {1}", file.Path, ioex.Message));
                                }
                            }
                            else
                            {
                                //syncItem.UploadDestination = string.Format("https://drive.google.com/open?id={0}", uploadedFile.Id);
                            }

                            // Don't delete files in log folder's root.
                            if (googlelogFolder.Name != localSubFolder.Name && uploadedFile != null && p.CanDeleteOldFiles == true)
                            {
                                // If build is too old (180days), delete it.
                                if (DateTime.Now.Subtract(file.DateCreated.DateTime).Days > p.DaysToDelete)
                                {
                                    filesToDelete.Add(file);
                                    UpdateStatus(TraceLevel.Warning, string.Format("Deleted archived\t{0} created on {1}", file.Path, file.DateCreated.Date));
                                }
                            }
                        }

                        UpdateStatus(TraceLevel.Info, string.Format("{0} files is going to delete.", filesToDelete.Count));
                        while (filesToDelete.Count > 0)
                        {
                            if (cancelToken.IsCancellationRequested)
                            {
                                UpdateStatus(TraceLevel.Info, "Backup cancelled");
                                cancelToken.ThrowIfCancellationRequested();
                                break;
                            }
                            StorageFile file = null;
                            try
                            {
                                file = filesToDelete.ElementAt(0);
                                filesToDelete.RemoveAt(0);
                                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                                UpdateStatus(TraceLevel.Warning, string.Format("Deleted permanently\t{0}", file.Path));
                            }
                            catch (System.IO.FileNotFoundException ioex)
                            {
                                Debug.WriteLine(ioex.Message);
                                UpdateStatus(TraceLevel.Warning, string.Format("Delete failed\t{0} message {1}", file.Path, ioex.Message));
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

        /// <summary>
        /// Parse the given folder and its subfolders to list the valid folders we have to proceed, or folders we already uploaded.
        /// </summary>
        /// <param name="localFolder">StorageFolder instance to parse</param>
        /// <param name="validateItemFunc"></param>
        /// <param name="ignoreItemFunc"></param>
        /// <param name="keepFolderFunc"></param>
        /// <param name="googleFolderId"></param>
        /// <param name="googleDrive"></param>
        /// <param name="progress"></param>
        /// <param name="cancelToken"></param>
        /// <returns></returns>
        public async Task<IReadOnlyList<SyncItem>> ParseFoldersAsync(ParseFolderParams @params)
        {
            List<SyncItem> resultFolders = new List<SyncItem>();
            List<SyncItem> recursiveFolders = new List<SyncItem>();

            try
            {
                KeyValuePair<File, StorageFolder> parentFolder = @params.LocalFolder;
                foreach (IStorageItem storageItem in (await @params.LocalFolder.Value.GetItemsAsync()))
                {
                    if (@params.CancelToken.IsCancellationRequested)
                    {
                        UpdateStatus(TraceLevel.Info, "Parse cancelled");
                        @params.CancelToken.ThrowIfCancellationRequested();
                        break;
                    }

                    UpdateStatus(TraceLevel.Verbose, storageItem.Path);

                    if (@params.IgnoreItemFunc(storageItem))
                    {
                        // Skip if the item should ignore.
                        continue;
                    }
                    else
                    {
                        // Check if storageItem belongs to which folder on Google drive
                        File matchingGoogleFolder = await @params.SelectGoogleFolderFunc(@params.GoogleDrive, @params.LocalFolder.Key, @params.GoogleItems, storageItem, p);
                        // Update items from new folder
                        if (@params.LocalFolder.Key != matchingGoogleFolder)
                        {
                            @params.GoogleItems = GoogleDriveItems(@params.GoogleDrive, matchingGoogleFolder.Id, SortOrder.NameAsc);
                        }
                        @params.LocalFolder = new KeyValuePair<File, StorageFolder>(matchingGoogleFolder, storageItem.IsOfType(StorageItemTypes.Folder) ? storageItem as StorageFolder : @params.LocalFolder.Value);

                        if (await @params.ValidateItemFunc(storageItem))
                        {
                            // Prepare item if valid
                            SyncItem syncItem = await @params.PrepareItemFunc(@params.LocalFolder.Key.Id, @params.GoogleDrive, @params.GoogleItems, storageItem, p);
                            // Only items that will need to upload or delete should add to list.
                            if (syncItem.UploadDestination == null || (p.CanDeleteOldFiles == true && syncItem.CanDeleteLocal == true && DateTime.Now.Subtract(storageItem.DateCreated.DateTime).Days > p.DaysToDelete))
                            {
                                Utility.UIThreadExecute(() => { listSyncItems.Add(syncItem); });
                                resultFolders.Add(syncItem);
                            }                            
                        }
                        else
                        {
                            if (storageItem.IsOfType(StorageItemTypes.Folder))
                            {
                                // Go deeper
                                recursiveFolders.AddRange(await ParseFoldersAsync(@params));
                                @params.LocalFolder = parentFolder;
                            }
                        }
                    }
                }

                resultFolders = @params.KeepFolderFunc(resultFolders);

                if (recursiveFolders != null)
                    resultFolders.AddRange(recursiveFolders);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                UpdateStatus(TraceLevel.Error, string.Format("Failed parse: {0} error: {1}", @params.LocalFolder.Value.Path, ex.Message));
            }

            return resultFolders.AsReadOnly();
        }

        public void SyncItemsAsync(IReadOnlyList<SyncItem> listItems, DriveService googleDrive, IProgress<long> rogress, CancellationToken cancelToken)
        {
            Parallel.ForEach(listItems, new ParallelOptions { MaxDegreeOfParallelism = 2 }, (syncItem, state) =>
            {
                try
                {
                    if (cancelToken.IsCancellationRequested)
                    {
                        UpdateStatus(TraceLevel.Info, "Backup cancelled");
                        cancelToken.ThrowIfCancellationRequested();
                    }

                    Utility.UIThreadExecute(() =>
                    {
                        listViewStatus.SelectedItem = syncItem;
                    });

                    Task taskZipUpload = Task.Run(async () =>
                    {
                        await ZipUploadItemAsync(syncItem, googleDrive);

                        if (syncItem.UploadDestination != null)
                        {
                            syncItem.Progress = 100;
                            syncItem.Status = "Done.";
                            // Release objects.
                            syncItem.BackupSource = null;
                            syncItem.UploadDestination = null;
                        }
                    });
                    taskZipUpload.Wait();
                }
                catch (System.OperationCanceledException cancelEx)
                {
                    Debug.WriteLine(cancelEx.Message);
                    UpdateStatus(TraceLevel.Error, string.Format("Cancelled upload: {0} error: {1}", syncItem.BackupSource.Path, cancelEx.Message));

                    state.Break();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.Message);
                    UpdateStatus(TraceLevel.Error, string.Format("Sync failed\t{0} message {1}", syncItem.BackupSource.Path, ex.Message));
                }
            });
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

        private static string GetLogSubfolderName(string itemName, int folderMaxCount)
        {
            string folderName = "0";
            string pattern = "^(\\d+)";

            // Instantiate the regular expression object.
            Regex r = new Regex(pattern, RegexOptions.IgnoreCase);

            // Match the regular expression pattern against a text string.
            Match m = r.Match(itemName);
            if (m.Success)
            {
                folderName = Convert.ToString(Convert.ToInt64(m.Value) / folderMaxCount * folderMaxCount);
            }
            else
            {
                // branchinfo items start with product name
                pattern = "^(.+)-(\\d+)";
                Regex r2 = new Regex(pattern, RegexOptions.IgnoreCase);
                Match m2 = r2.Match(itemName);
                if (m2.Success)
                    folderName = m2.Groups[1].Value;
            }

            return folderName;
        }

        public static async Task<File> GoogleDriveCreateFolderAsync(DriveService driveService, string parent, string itemName)
        {
            File googleFolder = null;
            try
            {
                googleFolder = GoogleDriveIsItemExist(driveService, parent, itemName, "application/vnd.google-apps.folder");
                if (googleFolder == null)
                {
                    File folderMetadata = new File
                    {
                        Name = itemName,
                        MimeType = "application/vnd.google-apps.folder",
                        Parents = new List<string> { parent }
                    };
                    FilesResource.CreateRequest requestUpload = driveService.Files.Create(folderMetadata);
                    googleFolder = await requestUpload.ExecuteAsync();
                    //UpdateStatus(TraceLevel.Info, string.Format("Folder created\t{0} at {1}", itemName, googleFolder.Id));
                }
                else
                {
                    //UpdateStatus(TraceLevel.Info, string.Format("Folder found\t{0} at {1}", itemName, googleFolder.Id));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                //UpdateStatus(TraceLevel.Error, string.Format("Folder create failed\t{0} parent {1} message {2}", itemName, parent, ex.Message));
            }
            return googleFolder;
        }

        public static FileList GoogleDriveItems(DriveService driveService, string parent, SortOrder order)
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
                    nextPageToken = result.NextPageToken;
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
                //UpdateStatus(TraceLevel.Error, string.Format("Folder list failed\t{0} message {1}", parent, ex.Message));
            }

            return sortedResult;
        }

        public static File GoogleDriveIsItemExist(FileList googleItems, string itemName, string contentType)
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

                throw new Exception(string.Format("File query failed\t{0} message {1}", itemName, ex.Message));
            }
            return googleItem;
        }

        #region Log valiation functions

        private static Task<bool> IsValidLogItemAsync(IStorageItem storageItem)
        {
            if (storageItem.IsOfType(StorageItemTypes.File))
            {
                string fileType = (storageItem as StorageFile).FileType.ToUpper();
                if (fileType == ".LOG" ||
                    fileType == ".CFG" ||
                    fileType == ".ZIP" ||
                    fileType == ".P4LOG" ||
                    fileType == ".REL" ||
                    fileType == ".RELNOTE" ||
                    fileType == ".INFO" ||
                    fileType == ".TXT")
                {
                    return Task.FromResult(true);
                }
            }

            return Task.FromResult(false);
        }

        private static bool IsIgnoredLogItemAsync(IStorageItem storageItem)
        {
            if (storageItem.Name == "ZipFolders.exe" ||
                storageItem.Name == ".DS_Store" ||
                storageItem.Path.StartsWith("V:\\autotest") ||
                storageItem.Path.StartsWith("V:\\bin") ||
                storageItem.Path.StartsWith("V:\\binarydepot") ||
                storageItem.Path.StartsWith("V:\\builddir") ||
                storageItem.Path.StartsWith("V:\\git") ||
                storageItem.Path.StartsWith("V:\\orion"))
                return true;

            if (storageItem.IsOfType(StorageItemTypes.File))
            {
                string fileType = (storageItem as StorageFile).FileType.ToUpper();
                if (fileType == ".GZ")
                    return true;
            }

            return false;
        }

        private static async Task<SyncItem> PrepareLogItemAsync(string googleFolderId, DriveService googleDrive, FileList googleItems, IStorageItem storageItem, Payload p)
        {
            SyncItem syncItem = new SyncItem();
            try
            {
                syncItem.BackupSource = storageItem;
                syncItem.ParentFolderId = googleFolderId;
                syncItem.Status = "Found " + storageItem.Path;

                if (storageItem.IsOfType(StorageItemTypes.File))
                {
                    StorageFile file = storageItem as StorageFile;
                    // Set google file name as name of the extracted file if it's zip file.
                    string googleFilename = file.FileType.ToUpper() == ".ZIP" ? file.DisplayName : file.Name;

                    // Change the name so that we won't get confused when search on Google drive
                    if (file.Path.StartsWith("V:\\branchinfo"))
                        googleFilename = System.IO.Path.ChangeExtension(googleFilename, ".branchinfo.log");
                    else if (file.Path.StartsWith("V:\\ftplog"))
                        googleFilename = System.IO.Path.ChangeExtension(googleFilename, ".ftplog.log");
                    else if (file.Path.StartsWith("V:\\isoconfig"))
                        googleFilename = System.IO.Path.ChangeExtension(googleFilename, ".isoconfig.log");
                    else if (file.Path.StartsWith("V:\\isovolume"))
                        googleFilename = System.IO.Path.ChangeExtension(googleFilename, ".isovolume.log");
                    else if (file.Path.StartsWith("V:\\relnoteconfig"))
                        googleFilename = System.IO.Path.ChangeExtension(googleFilename, ".relnoteconfig.log");
                    else if (file.Path.StartsWith("V:\\log"))
                        googleFilename = System.IO.Path.ChangeExtension(googleFilename, ".log");
                    else
                        googleFilename += ".log";

                    // Check if we already have item exist in Google drive.
                    File uploadedFile = GoogleDriveIsItemExist(googleItems, googleFilename, "text/plain");

                    if (uploadedFile == null && file.FileType.ToUpper() == ".ZIP")
                    {
                        // Unzip and retrieve the log file, as we are not sending zipped logs, it will be easier to view and search on Google Drive.
                        StorageFile tempZipFile = await file.CopyAsync(ApplicationData.Current.TemporaryFolder, file.Name, NameCollisionOption.ReplaceExisting);
                        // Delete extracted file if exists.
                        IStorageItem tempExtractedFile = await ApplicationData.Current.TemporaryFolder.TryGetItemAsync(file.DisplayName);
                        if (tempExtractedFile != null)
                            await tempExtractedFile.DeleteAsync();
                        // Extract the zip file
                        string tempPath = ApplicationData.Current.TemporaryFolder.Path;
                        ZipFile.ExtractToDirectory(tempZipFile.Path, tempPath);
                        StorageFile extractedFile = await ApplicationData.Current.TemporaryFolder.GetFileAsync(file.DisplayName);
                        if (extractedFile.Name != googleFilename)
                            await extractedFile.RenameAsync(googleFilename, NameCollisionOption.ReplaceExisting);
                        syncItem.ExtractedSource = extractedFile;
                        await tempZipFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }

                    syncItem.Name = googleFilename;
                    syncItem.UploadDestination = uploadedFile;

                    if (syncItem.UploadDestination != null)
                        syncItem.Status = "Exists in Google Drive";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                throw new Exception(string.Format("Failed parse: {0} error: {1}", storageItem.Path, ex.Message), ex);
            }
            return syncItem;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="googleDrive"></param>
        /// <param name="googleFolder">Parent folder</param>
        /// <param name="googleItems">Items in parent's folder</param>
        /// <param name="storageItem"></param>
        /// <returns></returns>
        private static async Task<File> SelectLogGoogleFolderAsync(DriveService googleDrive, File googleFolder, FileList googleItems, IStorageItem storageItem, Payload p)
        {
            File matchingGoogleFolder = googleFolder;

            if (storageItem.IsOfType(StorageItemTypes.Folder))
            {
                // We don't use Archived and range folder when backup, replace folder ID with parent's.
                StorageFolder folder = storageItem as StorageFolder;
                Regex regexRange = new Regex("\\d+-\\d+", RegexOptions.IgnoreCase); // 0-49999
                                                                                    // Match the regular expression pattern against a text string.
                if (storageItem.Name == "Archived" ||
                    storageItem.Name == "Temp" ||
                    regexRange.Match(storageItem.Name).Success)
                {

                }
                else
                {
                    // Check if folder exist in Google Drive
                    matchingGoogleFolder = GoogleDriveIsItemExist(googleItems, storageItem.Name, "application/vnd.google-apps.folder");
                    if (matchingGoogleFolder == null)
                    {
                        // Not exist, create or get one
                        matchingGoogleFolder = await GoogleDriveCreateFolderAsync(googleDrive, googleFolder.Id, storageItem.Name);
                    }
                }
            }
            else
            {
                // If item belongs to subfolder other than parent folder, we have to use it
                string matchingSubfolderName = GetLogSubfolderName(storageItem.Name, p.FolderMaxItems);
                if (googleFolder.Name != matchingSubfolderName)
                {
                    // Special case for branchinfo that will always create subfolder within branchinfo.
                    // Not subfolder, create or get one
                    matchingGoogleFolder = await GoogleDriveCreateFolderAsync(googleDrive, storageItem.Path.StartsWith("V:\\branchinfo") ? "0B8j6UJY_E28CYnp2TU50RU9uVjA" : googleFolder.Id, matchingSubfolderName);
                }
            }

            return matchingGoogleFolder;
        }

        private static List<SyncItem> IsKeepLogItem(List<SyncItem> items)
        {
            return items;
        }
        #endregion
        #region Build valiation functions
        private static Task<bool> IsValidBuildItemAsync(IStorageItem storageItem)
        {
            if (storageItem.IsOfType(StorageItemTypes.Folder))
            {
                // Instantiate the regular expression object.
                Regex regexServer = new Regex("_LOGID\\d{6}(_ISO)?$", RegexOptions.IgnoreCase); // 20.XB000.VSPX10.Suite2_20.1.0.18_VIDEOULTIMATE(QA)-SUITE(RELEASE)_LOGID563044
                Regex regexManual = new Regex("_LOGID\\d{8}(_ISO)?$", RegexOptions.IgnoreCase); // Main-Branch_20.0.0.132_PHOTOULT(QA)-RETAIL(RELEASE-AMAZON)_LOGID20170725_ISO
                Regex regexPDB = new Regex("\\d+\\.\\d+\\.\\d+\\.\\d+ld$", RegexOptions.IgnoreCase); // AfterShotPro_3.3.0.250ld
                Regex regexDDR = new Regex("_LOGID\\d{8}\\.\\d{2}$", RegexOptions.IgnoreCase); // DDR OEM_3.62_DDR362-JP.(Stage8a)_LOGID20171215.15
                Regex regexTVX = new Regex("LOGID\\d{6}\\)_\\d{8}$", RegexOptions.IgnoreCase); //TVx_SDK_withBDMoveWin10Patch(2.3.0.47.LOGID564642)_20171221

                // Match the regular expression pattern against a text string.
                if (regexServer.Match(storageItem.Name).Success ||
                    regexManual.Match(storageItem.Name).Success ||
                    regexPDB.Match(storageItem.Name).Success ||
                    regexDDR.Match(storageItem.Name).Success ||
                    regexTVX.Match(storageItem.Name).Success ||
                    storageItem.Name.EndsWith("_CDA"))
                {
                    return Task.FromResult(true);
                }
            }
            else
            {
                string fileType = (storageItem as StorageFile).FileType.ToUpper();
                // Instantiate the regular expression object.
                // _1.0.0.100_PDB.exe
                // _1.0.0.100_DebugKit.exe
                // _1.0.0.100.exe
                Regex regexExe = new Regex("\\d+\\.\\d+\\.\\d+\\.\\d+[a-z]?(_DebugKit|_PDB)?\\.exe$", RegexOptions.IgnoreCase);

                // Match the regular expression pattern against a text string.
                if (fileType == ".ISO" ||
                    fileType == ".ZIP" ||
                    fileType == ".RAR" ||
                    regexExe.Match(storageItem.Name).Success)
                {
                    return Task.FromResult(true);
                }
            }

            return Task.FromResult(false);
        }

        private static bool IsIgnoredBuildItemAsync(IStorageItem storageItem)
        {
            if (storageItem.Name == "ZipFolders.exe" ||
                storageItem.Name == ".DS_Store" ||
                storageItem.Name == ".Trash-1000" ||
                storageItem.Path == "H:\\Cache" ||
                storageItem.Path == "H:\\DVD10_BUILDS\\Tools" ||
                storageItem.Path.Contains("_BUILDS\\bonus-features") ||
                storageItem.Path.Contains("_BUILDS\\ContentHD") ||
                storageItem.Path.Contains("_BUILDS\\ContentHD_VLP"))
                return true;

            return false;
        }

        private static Task<SyncItem> PrepareBuildItemAsync(string googleFolderId, DriveService googleDrive, FileList googleItems, IStorageItem storageItem, Payload p)
        {
            // If folder is build, check if zip exist in Google Drive
            string itemName = storageItem.IsOfType(StorageItemTypes.Folder) ? storageItem.Name + ".zip" : storageItem.Name;
            string contentType = storageItem.IsOfType(StorageItemTypes.Folder) ? "application/x-zip-compressed" : (storageItem as StorageFile).ContentType;
            File uploadedFile = GoogleDriveIsItemExist(googleItems, itemName, contentType);

            SyncItem syncItem = new SyncItem();
            syncItem.BackupSource = storageItem;
            syncItem.Status = "Found " + storageItem.Path;
            syncItem.UploadDestination = uploadedFile;
            syncItem.ParentFolderId = googleFolderId;
            if (syncItem.UploadDestination != null)
                syncItem.Status = "Exists in Google Drive";

            return Task.FromResult(syncItem);
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="googleDrive"></param>
        /// <param name="googleFolder">Parent folder</param>
        /// <param name="googleItems">Items in parent's folder</param>
        /// <param name="storageItem"></param>
        /// <returns></returns>
        private static async Task<File> SelectBuildGoogleFolderAsync(DriveService googleDrive, File googleFolder, FileList googleItems, IStorageItem storageItem, Payload p)
        {
            File matchingGoogleFolder = googleFolder;

            if (storageItem.IsOfType(StorageItemTypes.Folder))
            {
                // Instantiate the regular expression object.
                Regex regexServer = new Regex("_LOGID\\d{6}(_ISO)?$", RegexOptions.IgnoreCase); // 20.XB000.VSPX10.Suite2_20.1.0.18_VIDEOULTIMATE(QA)-SUITE(RELEASE)_LOGID563044
                Regex regexManual = new Regex("_LOGID\\d{8}(_ISO)?$", RegexOptions.IgnoreCase); // Main-Branch_20.0.0.132_PHOTOULT(QA)-RETAIL(RELEASE-AMAZON)_LOGID20170725_ISO
                Regex regexPDB = new Regex("\\d+\\.\\d+\\.\\d+\\.\\d+ld$", RegexOptions.IgnoreCase); // AfterShotPro_3.3.0.250ld
                Regex regexDDR = new Regex("_LOGID\\d{8}\\.\\d{2}$", RegexOptions.IgnoreCase); // DDR OEM_3.62_DDR362-JP.(Stage8a)_LOGID20171215.15
                Regex regexTVX = new Regex("LOGID\\d{6}\\)_\\d{8}$", RegexOptions.IgnoreCase); //TVx_SDK_withBDMoveWin10Patch(2.3.0.47.LOGID564642)_20171221

                // Match the regular expression pattern against a text string.
                if (regexServer.Match(storageItem.Name).Success ||
                    regexManual.Match(storageItem.Name).Success ||
                    regexPDB.Match(storageItem.Name).Success ||
                    regexDDR.Match(storageItem.Name).Success ||
                    regexTVX.Match(storageItem.Name).Success ||
                    storageItem.Name.EndsWith("_CDA"))
                {

                }
                else
                {
                    // Check if folder exist in Google Drive
                    matchingGoogleFolder = GoogleDriveIsItemExist(googleItems, storageItem.Name, "application/vnd.google-apps.folder");
                    if (matchingGoogleFolder == null)
                    {
                        // Not exist, create or get one
                        matchingGoogleFolder = await GoogleDriveCreateFolderAsync(googleDrive, googleFolder.Id, storageItem.Name);
                    }
                }
            }

            return matchingGoogleFolder;
        }

        private static List<SyncItem> IsKeepBuildItem(List<SyncItem> items)
        {
            foreach (SyncItem item in items)
            {
                if (item.BackupSource.Path.Contains("_BUILDS\\bonus-features") ||
                    item.BackupSource.Path.Contains("_BUILDS\\ContentHD") ||
                    item.BackupSource.Path.Contains("_BUILDS\\ContentHD_VLP"))
                {
                    item.CanDeleteLocal = false;
                }
            }
            return items;
        }
        #endregion
        #region BinaryDepot valiation functions
        private static async Task<bool> IsValidBinaryDepotItemAsync(IStorageItem storageItem)
        {
            if (storageItem.IsOfType(StorageItemTypes.Folder))
            {
                StorageFolder folder = storageItem as StorageFolder;
                // If folder is build binary, it has either "BackupApp.exe" or "RawData" folders
                if ((await folder.TryGetItemAsync("RawData")) != null ||
                (await folder.TryGetItemAsync("RealData")) != null ||
                (await folder.TryGetItemAsync("BackupApp.exe")) != null)    // For GoldenGate\Fujitsu17Q2
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsIgnoredBinaryDepotItemAsync(IStorageItem storageItem)
        {
            if (storageItem.Name == "ZipFolders.exe")
                return true;

            return false;
        }

        private static Task<SyncItem> PrepareBinaryDepotItemAsync(string googleFolderId, DriveService googleDrive, FileList googleItems, IStorageItem storageItem, Payload p)
        {
            // If folder is build, check if zip exist in Google Drive
            string itemName = storageItem.IsOfType(StorageItemTypes.Folder) ? storageItem.Name + ".zip" : storageItem.Name;
            string contentType = storageItem.IsOfType(StorageItemTypes.Folder) ? "application/x-zip-compressed" : (storageItem as StorageFile).ContentType;
            File uploadedFile = GoogleDriveIsItemExist(googleItems, itemName, contentType);

            SyncItem syncItem = new SyncItem();
            syncItem.BackupSource = storageItem;
            syncItem.Status = "Found " + storageItem.Path;
            syncItem.UploadDestination = uploadedFile;
            syncItem.ParentFolderId = googleFolderId;
            if (syncItem.UploadDestination != null)
                syncItem.Status = "Exists in Google Drive";

            return Task.FromResult(syncItem);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="googleDrive"></param>
        /// <param name="googleFolder">Parent folder</param>
        /// <param name="googleItems">Items in parent's folder</param>
        /// <param name="storageItem"></param>
        /// <returns></returns>
        private static async Task<File> SelectBinaryDepotGoogleFolderAsync(DriveService googleDrive, File googleFolder, FileList googleItems, IStorageItem storageItem, Payload p)
        {
            File matchingGoogleFolder = googleFolder;

            if (storageItem.IsOfType(StorageItemTypes.Folder))
            {
                StorageFolder folder = storageItem as StorageFolder;
                // If folder is build binary, it has either "BackupApp.exe" or "RawData" folders
                if ((await folder.TryGetItemAsync("RawData")) != null ||
                (await folder.TryGetItemAsync("RealData")) != null ||
                (await folder.TryGetItemAsync("BackupApp.exe")) != null)    // For GoldenGate\Fujitsu17Q2
                {
                }
                else
                {
                    // Check if folder exist in Google Drive
                    matchingGoogleFolder = GoogleDriveIsItemExist(googleItems, storageItem.Name, "application/vnd.google-apps.folder");
                    if (matchingGoogleFolder == null)
                    {
                        // Not exist, create or get one
                        matchingGoogleFolder = await GoogleDriveCreateFolderAsync(googleDrive, googleFolder.Id, storageItem.Name);
                    }
                }
            }

            return matchingGoogleFolder;
        }

        private static List<SyncItem> IsKeepBinaryDepotItem(List<SyncItem> items)
        {
            return items;
        }
        #endregion
        #region ComponentSDK valiation functions
        private static Task<bool> IsValidComponentSDKItemAsync(IStorageItem storageItem)
        {
            if (storageItem.IsOfType(StorageItemTypes.Folder))
            {
                StorageFolder folder = storageItem as StorageFolder;
                // If folder is ComponentSDK, it has name in "version.major.minor.build" format
                string pattern1 = "^\\d+\\.\\d+\\.\\d+\\.\\d+[a-z]?$";    // 1.0.0.100a
                string pattern2 = "^\\d+\\.\\d+BUILD\\d+\\.\\d+$";  // 10.0BUILD000.02

                // Match the regular expression pattern against a text string.
                Match match1 = Regex.Match(folder.Name, pattern1, RegexOptions.IgnoreCase);
                Match match2 = Regex.Match(folder.Name, pattern2, RegexOptions.IgnoreCase);
                if (match1.Success || match2.Success)
                {
                    return Task.FromResult(true);
                }
            }
            else
            {
                StorageFile file = storageItem as StorageFile;
            }

            return Task.FromResult(false);
        }

        private static bool IsIgnoredComponentSDKItem(IStorageItem storageItem)
        {
            if (storageItem.Name == "NIGHTLY" ||
                storageItem.Name == "SymbolServer" ||
                storageItem.Path.StartsWith("T:\\Cache"))
                return true;

            return false;
        }

        private static Task<SyncItem> PrepareComponentSDKItemAsync(string googleFolderId, DriveService googleDrive, FileList googleItems, IStorageItem storageItem, Payload p)
        {
            // If folder is build, check if zip exist in Google Drive
            string itemName = storageItem.IsOfType(StorageItemTypes.Folder) ? storageItem.Name + ".zip" : storageItem.Name;
            string contentType = storageItem.IsOfType(StorageItemTypes.Folder) ? "application/x-zip-compressed" : (storageItem as StorageFile).ContentType;
            File uploadedFile = GoogleDriveIsItemExist(googleItems, itemName, contentType);

            SyncItem syncItem = new SyncItem();
            syncItem.BackupSource = storageItem;
            syncItem.Status = "Found " + storageItem.Path;
            syncItem.UploadDestination = uploadedFile;
            syncItem.ParentFolderId = googleFolderId;
            if (syncItem.UploadDestination != null)
                syncItem.Status = "Exists in Google Drive";

            return Task.FromResult(syncItem);
        }

        private static async Task<File> SelectComponentSDKGoogleFolderAsync(DriveService googleDrive, File googleFolder, FileList googleItems, IStorageItem storageItem, Payload p)
        {
            File matchingGoogleFolder = googleFolder;

            if (storageItem.IsOfType(StorageItemTypes.Folder))
            {
                // If folder is ComponentSDK, it has name in "version.major.minor.build" format
                string pattern1 = "^\\d+\\.\\d+\\.\\d+\\.\\d+[a-z]?$";    // 1.0.0.100a
                string pattern2 = "^\\d+\\.\\d+BUILD\\d+\\.\\d+$";  // 10.0BUILD000.02

                // Match the regular expression pattern against a text string.
                Match match1 = Regex.Match(storageItem.Name, pattern1, RegexOptions.IgnoreCase);
                Match match2 = Regex.Match(storageItem.Name, pattern2, RegexOptions.IgnoreCase);
                if (match1.Success || match2.Success)
                {

                }
                else
                {
                    // Check if folder exist in Google Drive
                    matchingGoogleFolder = GoogleDriveIsItemExist(googleItems, storageItem.Name, "application/vnd.google-apps.folder");
                    if (matchingGoogleFolder == null)
                    {
                        // Not exist, create or get one
                        matchingGoogleFolder = await GoogleDriveCreateFolderAsync(googleDrive, googleFolder.Id, storageItem.Name);
                    }
                }
            }

            return matchingGoogleFolder;
        }

        private static List<SyncItem> IsKeepComponentSDKFolderAsync(List<SyncItem> items)
        {
            List<SyncItem> keepItems = new List<SyncItem>();
            string pattern = "^(?<version>\\d+)\\.(?<major>\\d+)\\.(?<minor>\\d+)\\.(?<build>\\d+)(?<extended>[a-z]?)$";
            // If the list is not empty and match the version pattern then we have to sort it.
            if (items.Count > 0 && Regex.Match(items.First().BackupSource.Name, pattern, RegexOptions.IgnoreCase).Success)
            {
                items.Sort(delegate (SyncItem item1, SyncItem item2)
                {
                    Match match1 = Regex.Match(item1.BackupSource.Name, pattern, RegexOptions.IgnoreCase);
                    Match match2 = Regex.Match(item2.BackupSource.Name, pattern, RegexOptions.IgnoreCase);

                    if (match1.Success && match2.Success)
                    {
                        // Version
                        int version1 = Convert.ToInt16(match1.Groups["version"].Value);
                        int version2 = Convert.ToInt16(match2.Groups["version"].Value);
                        if (version1 > version2) return 1;
                        else if (version1 < version2) return -1;

                        // Major
                        int major1 = Convert.ToInt16(match1.Groups["major"].Value);
                        int major2 = Convert.ToInt16(match2.Groups["major"].Value);
                        if (major1 > major2) return 1;
                        else if (major1 < major2) return -1;

                        // Minor
                        int minor1 = Convert.ToInt16(match1.Groups["minor"].Value);
                        int minor2 = Convert.ToInt16(match2.Groups["minor"].Value);
                        if (minor1 > minor2) return 1;
                        else if (minor1 < minor2) return -1;

                        // Build
                        int build1 = Convert.ToInt16(match1.Groups["build"].Value);
                        int build2 = Convert.ToInt16(match2.Groups["build"].Value);
                        if (build1 > build2) return 1;
                        else if (build1 < build2) return -1;

                        // Extended
                        char extended1;
                        char extended2;
                        char.TryParse(match1.Groups["extended"].Value, out extended1);
                        char.TryParse(match2.Groups["extended"].Value, out extended2);
                        return Comparer<char>.Default.Compare(extended1, extended2);
                    }
                    else
                    {
                        return Comparer<string>.Default.Compare(item1.BackupSource.Name, item2.BackupSource.Name);
                    }
                });
                items.Reverse();

                string prevVersionMajor = "", curVersionMajor = "";
                int curCount = 0, keepCount = 3;
                bool keepItem;
                foreach (SyncItem item in items)
                {
                    keepItem = true;
                    Match matchVer = Regex.Match(item.BackupSource.Name, pattern, RegexOptions.IgnoreCase);
                    if (matchVer.Success)
                    {
                        curVersionMajor = matchVer.Groups["version"].Value + "." + matchVer.Groups["major"].Value;
                        if (curVersionMajor != prevVersionMajor)
                        {
                            prevVersionMajor = curVersionMajor;
                            curCount = 0;
                        }

                        keepItem = curCount < keepCount ? true : false;
                        curCount++;
                    }

                    item.CanDeleteLocal = !keepItem;
                    item.Status = item.CanDeleteLocal ? "Folder can delete" : "Folder won't delete";
                    keepItems.Add(item);
                }
            }
            else
            {
                keepItems = items;
            }

            return keepItems;
        }
        #endregion
        public async Task<File> UploadAsync(DriveService driveService, IList<string> parents, StorageFile file, string itemName, IProgress<long> progress, CancellationToken cancelToken)
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

                UserCredential credential = (UserCredential)driveService.HttpClientInitializer;
                if (credential.Token.IsExpired(Google.Apis.Util.SystemClock.Default))
                {
                    try
                    {
                        TokenResponse token = new TokenResponse
                        {
                            AccessToken = credential.Token.AccessToken,
                            RefreshToken = credential.Token.RefreshToken
                        };

                        GoogleClientSecrets clientSecrets;
                        StorageFile fileSecret = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/client_secret.json"));
                        using (IRandomAccessStream fStream = await fileSecret.OpenAsync(FileAccessMode.Read))
                        using (DataReader reader = new DataReader(fStream.GetInputStreamAt(0)))
                        {
                            byte[] bytes = new byte[fStream.Size];
                            await reader.LoadAsync((uint)fStream.Size);
                            reader.ReadBytes(bytes);
                            System.IO.Stream stream = new System.IO.MemoryStream(bytes);
                            clientSecrets = GoogleClientSecrets.Load(stream);
                        }

                        IAuthorizationCodeFlow flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
                        {
                            ClientSecrets = clientSecrets.Secrets,
                            Scopes = new[] { DriveService.Scope.Drive }

                        });
                        credential = new UserCredential(flow, "user", token);

                        bool success = credential.RefreshTokenAsync(CancellationToken.None).Result;
                    }
                    catch (AggregateException ex)
                    {
                        Debug.Write("Credential failed, " + ex.Message);
                    }
                }

                // Step 1: Start a resumable session
                HttpWebRequest httpRequest = (HttpWebRequest)WebRequest.Create("https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable");
                httpRequest.Headers["Content-Type"] = "application /json; charset=UTF-8";
                httpRequest.Headers["Content-Length"] = json.Length.ToString();
                httpRequest.Headers["X-Upload-Content-Type"] = file.ContentType;
                httpRequest.Headers["X-Upload-Content-Length"] = fileSize.ToString();
                httpRequest.Headers["Authorization"] = "Bearer " + credential.Token.AccessToken;
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
                    ulong chunkSize = 20 * 1024 * 1024; // 20MB
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
                                // Report progress to caller
                                progress?.Report(Convert.ToInt64(writeSize));
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
                        {
                            progress?.Report(Convert.ToInt64(writeSize));
                            break;
                        }
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
                {
                    uploadedFile = result.Files[0];
                    UpdateStatus(TraceLevel.Info, string.Format("File retrieved from Google\t{0}", uploadedFile.Name));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                UpdateStatus(TraceLevel.Error, string.Format("File upload failed\t{0} message {1}", file.Path, ex.Message));
            }

            return uploadedFile;
        }

        public static async Task CopyFolderAsync(StorageFolder source, StorageFolder destinationContainer, IProgress<long> progress, CancellationToken cancelToken)
        {
            long size = 0;

            foreach (StorageFile file in await source.GetFilesAsync())
            {
                if (cancelToken.IsCancellationRequested)
                    cancelToken.ThrowIfCancellationRequested();

                try
                {
                    await file.CopyAsync(destinationContainer, file.Name, NameCollisionOption.ReplaceExisting);
                    size = Convert.ToInt64((await file.GetBasicPropertiesAsync()).Size);
                    progress?.Report(size);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(string.Format("Failed to copy file in folder: {0} error: {1}", file.Name, ex.Message));
                }
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();

            foreach (StorageFolder folder in await source.GetFoldersAsync())
            {
                if (cancelToken.IsCancellationRequested)
                    cancelToken.ThrowIfCancellationRequested();

                StorageFolder destSubFolder = await destinationContainer.CreateFolderAsync(folder.Name, CreationCollisionOption.ReplaceExisting);
                await CopyFolderAsync(folder, destSubFolder, progress, cancelToken);
            }
        }

        private async Task<IReadOnlyList<IStorageFolder>> GetAllSubfoldersAsync(StorageFolder localFolder)
        {
            List<StorageFolder> folders = new List<StorageFolder>();
            folders.Add(localFolder);
            IReadOnlyList<IStorageFolder> subfolders = await localFolder.GetFoldersAsync();
            foreach (StorageFolder subfolder in subfolders)
            {
                List<StorageFolder> subfolders2 = (List<StorageFolder>)await GetAllSubfoldersAsync(subfolder);
                folders.AddRange(subfolders2);
            }

            return folders;
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

        private async Task<IReadOnlyList<KeyValuePair<SyncItem, bool>>> SortKeepStorageItemsAsync(IReadOnlyList<SyncItem> listItems, int keep)
        {
            List<KeyValuePair<SyncItem, bool>> sortedItems = new List<KeyValuePair<SyncItem, bool>>();
            try
            {
                List<SyncItem> localItems = new List<SyncItem>(listItems);
                string pattern = "^(?<version>\\d+)\\.(?<major>\\d+)\\.(?<minor>\\d+)\\.(?<build>\\d+)(?<extended>[a-z]?)$";

                bool isVerion = false;
                if (localItems.Count > 0)
                {
                    // Match the regular expression pattern against a text string.
                    Match matchVer = Regex.Match(localItems.First().BackupSource.Name, pattern, RegexOptions.IgnoreCase);
                    if (matchVer.Success)
                    {
                        isVerion = true;
                    }
                }

                if (isVerion)
                {
                    localItems.Sort(delegate (SyncItem item1, SyncItem item2)
                    {
                        Match match1 = Regex.Match(item1.BackupSource.Name, pattern, RegexOptions.IgnoreCase);
                        Match match2 = Regex.Match(item2.BackupSource.Name, pattern, RegexOptions.IgnoreCase);

                        if (match1.Success && match2.Success)
                        {
                            // Version
                            int version1 = Convert.ToInt16(match1.Groups["version"].Value);
                            int version2 = Convert.ToInt16(match2.Groups["version"].Value);
                            if (version1 > version2) return 1;
                            else if (version1 < version2) return -1;

                            // Major
                            int major1 = Convert.ToInt16(match1.Groups["major"].Value);
                            int major2 = Convert.ToInt16(match2.Groups["major"].Value);
                            if (major1 > major2) return 1;
                            else if (major1 < major2) return -1;

                            // Minor
                            int minor1 = Convert.ToInt16(match1.Groups["minor"].Value);
                            int minor2 = Convert.ToInt16(match2.Groups["minor"].Value);
                            if (minor1 > minor2) return 1;
                            else if (minor1 < minor2) return -1;

                            // Build
                            int build1 = Convert.ToInt16(match1.Groups["build"].Value);
                            int build2 = Convert.ToInt16(match2.Groups["build"].Value);
                            if (build1 > build2) return 1;
                            else if (build1 < build2) return -1;

                            // Extended
                            char extended1;
                            char extended2;
                            char.TryParse(match1.Groups["extended"].Value, out extended1);
                            char.TryParse(match2.Groups["extended"].Value, out extended2);
                            return Comparer<char>.Default.Compare(extended1, extended2);
                        }
                        else
                        {
                            return Comparer<string>.Default.Compare(item1.BackupSource.Name, item2.BackupSource.Name);
                        }
                    });
                    localItems.Reverse();


                    string prevVersionMajor = "", curVersionMajor = "";
                    int keepCount = 0;
                    bool keepItem;
                    foreach (SyncItem item in localItems)
                    {
                        keepItem = true;
                        Match matchVer = Regex.Match(item.BackupSource.Name, pattern, RegexOptions.IgnoreCase);
                        if (matchVer.Success)
                        {
                            curVersionMajor = matchVer.Groups["version"].Value + "." + matchVer.Groups["major"].Value;
                            if (curVersionMajor != prevVersionMajor)
                            {
                                prevVersionMajor = curVersionMajor;
                                keepCount = 0;
                            }

                            keepItem = keepCount < keep ? true : false;
                            keepCount++;
                        }

                        sortedItems.Add(new KeyValuePair<SyncItem, bool>(item, keepItem));
                    }
                }
                else
                {
                    foreach (SyncItem item in localItems)
                    {
                        sortedItems.Add(new KeyValuePair<SyncItem, bool>(item, true));
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                //UpdateStatus(TraceLevel.Error, string.Format("Failed to sort files in folder: {0} error: {1}", localFolder, ex.Message));
            }

            return sortedItems.AsReadOnly();

        }

        private async static Task<long> GetDirectorySize(StorageFolder rootFolder)
        {
            long size = 0;

            List<StorageFile> files = new List<StorageFile>();
            Stack<StorageFolder> folders = new Stack<StorageFolder>();
            folders.Push(rootFolder);
            while (folders.Count > 0)
            {
                StorageFolder currentFolder = folders.Pop();

                IReadOnlyList<StorageFolder> subFolders = await currentFolder.GetFoldersAsync();

                files.AddRange(await currentFolder.GetFilesAsync());

                foreach (StorageFolder folder in subFolders)
                    folders.Push(folder);
            }

            StorageFile f = files.First();


            Parallel.ForEach(files, (file) =>
            {
                //                long result = Task.Run(() => 
                //                {                   
                BasicProperties prop = Task.Run(async () =>
                {
                    try
                    {
                        return await file.GetBasicPropertiesAsync();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(string.Format("Failed GetBasicPropertiesAsync: {0} error: {1}", file.Path, ex.Message));
                    }
                    return null;
                }).Result;
                //                    return Convert.ToInt64(prop?.Size);
                //                }).Result;
                Interlocked.Add(ref size, Convert.ToInt64(prop?.Size));
            });


            return size;
        }

        private async Task ZipUploadItemAsync(SyncItem syncItem, DriveService googleDrive)
        {
            File uploadedFile = syncItem.UploadDestination;
            // If extracted file exist, use it.
            IStorageItem storageItem = syncItem.BackupSource;
            StorageFile tempFile = null;
            StorageFolder tempFolder = null;

            try
            {
                if (uploadedFile == null)
                {
                    if (storageItem.IsOfType(StorageItemTypes.Folder))
                    {
                        IReadOnlyList<IStorageItem> childItems = await (storageItem as StorageFolder).GetItemsAsync();
                        if (childItems.Count > 0)
                        {
                            // Find out the source size for progress report.
                            //syncItem.Status = "Begin size count";
                            //syncItem.Size = await GetDirectorySize(syncItem.BackupSource as StorageFolder);
                            //syncItem.Status = string.Format("Size {0}MB", syncItem.Size / 1024 / 1024);

                            // Zip and upload
                            // Workaround: http://stackoverflow.com/questions/33801760/
                            string tempName = DateTime.Now.ToString("HHmmssfff");
                            string zipPath = string.Format("{0}\\{1}.zip", ApplicationData.Current.TemporaryFolder.Path, tempName);
                            syncItem.Status = string.Format("Copying\t{0}", zipPath);
                            tempFolder = await p.TempFolder.CreateFolderAsync(tempName, CreationCollisionOption.GenerateUniqueName);

                            // tempName may changed to unique
                            if (tempFolder.Name != tempName)
                            {
                                tempName = tempFolder.Name;
                                zipPath = string.Format("{0}\\{1}.zip", ApplicationData.Current.TemporaryFolder.Path, tempName);
                                syncItem.Status = string.Format("Changed folder to\t{0}", zipPath);
                            }

                            await CopyFolderAsync(storageItem as StorageFolder, tempFolder, new Progress<long>(copiedBytes => syncItem.Received += copiedBytes), m_cts.Token);
                            syncItem.Status = "Copied and Zipping.";
                            //syncItem.Progress += 25;

                            ZipFile.CreateFromDirectory(tempFolder.Path, zipPath, CompressionLevel.Optimal, false);
                            tempFile = await p.TempFolder.GetFileAsync(tempName + ".zip");
                            syncItem.Status = "Zipped and uploading";
                            syncItem.Progress += 25;

                            // Upload to Google Drive
                            //int trial = 2;
                            //for (int i = 0; i < trial && uploadedFile == null; i++)
                            //{
                            syncItem.Sent = 0;
                            uploadedFile = await UploadAsync(googleDrive, new List<string> { syncItem.ParentFolderId }, tempFile, storageItem.Name + ".zip", new Progress<long>(sentBytes => syncItem.Sent += sentBytes), m_cts.Token);
                            //}

                            if (uploadedFile == null)
                            {
                                syncItem.Status = string.Format("Upload failed\t{0} as {1}", tempFile.Path, storageItem.Name);
                            }
                            else
                            {
                                syncItem.Status = string.Format("Uploaded\thttps://drive.google.com/open?id={0}", uploadedFile.Id);
                                syncItem.UploadDestination = uploadedFile;
                            }
                            syncItem.Progress += 25;

                            // Delete temp file and folder
                            if (tempFolder != null)
                                ThreadPoolTimer.CreateTimer((timer) => StorageItemDeleteTimerElpasedHandler(timer, tempFolder), TimeSpan.FromMilliseconds(10));
                            if (tempFile != null)
                                await tempFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                        }
                        else
                        {
                            syncItem.Status = "Folder is empty.";
                        }
                    }
                    else
                    {
                        // Not exist, upload to Google Drive
                        syncItem.Status = string.Format("Copying\t{0}", storageItem.Path);
                        if (syncItem.ExtractedSource == null)
                            tempFile = await (storageItem as StorageFile).CopyAsync(p.TempFolder, storageItem.Name, NameCollisionOption.ReplaceExisting);
                        else
                            tempFile = syncItem.ExtractedSource as StorageFile;
                        syncItem.Status = "Copied.";
                        syncItem.Progress += 50;

                        uploadedFile = await UploadAsync(googleDrive, new List<string> { syncItem.ParentFolderId }, tempFile, syncItem.Name, new Progress<long>(sentBytes => syncItem.Sent += sentBytes), m_cts.Token);
                        if (uploadedFile == null)
                        {
                            syncItem.Status = string.Format("Upload failed\t{0} as {1}", tempFile.Path, syncItem.Name);
                        }
                        else
                        {
                            syncItem.Status = string.Format("Uploaded\thttps://drive.google.com/open?id={0}", uploadedFile.Id);
                            syncItem.UploadDestination = uploadedFile;
                        }
                        syncItem.Progress += 25;
                        syncItem.ExtractedSource = null;

                        await tempFile?.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }
                }

                if (uploadedFile != null && p.CanDeleteOldFiles == true && syncItem.CanDeleteLocal == true)
                {
                    // If build is too old (180days), delete it.
                    if (DateTime.Now.Subtract(storageItem.DateCreated.DateTime).Days > p.DaysToDelete)
                    {
                        ThreadPoolTimer.CreateTimer((timer) => StorageItemDeleteTimerElpasedHandler(timer, storageItem), TimeSpan.FromMilliseconds(10));
                        syncItem.Status = string.Format("Deleted\t{0} created on {1}", storageItem.Path, storageItem.DateCreated.Date);
                    }
                }
            }
            catch (System.OperationCanceledException cancelEx)
            {
                Debug.WriteLine(cancelEx.Message);
                UpdateStatus(TraceLevel.Error, string.Format("Cancelled: {0} error: {1}", syncItem.BackupSource.Path, cancelEx.Message));

                // Delete temp file and folder
                if (tempFolder != null)
                    ThreadPoolTimer.CreateTimer((timer) => StorageItemDeleteTimerElpasedHandler(timer, tempFolder), TimeSpan.FromMilliseconds(10));
                await tempFile?.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
                UpdateStatus(TraceLevel.Error, string.Format("Failed delete: {0} error: {1}", syncItem.BackupSource.Path, ex.Message));
            }
        }

        private void buttonStartBackup_Click(object sender, RoutedEventArgs e)
        {
            if (m_backupFolders.Count > 0 && p.TempFolder != null)
            {
                m_cts = new CancellationTokenSource();
                // Create a timer to periodically check the build folders
                //                m_timerFolderCheck = ThreadPoolTimer.CreateTimer((timer) => FolderSyncTimerElpasedHandler(timer, m_backupFolders), TimeSpan.FromMilliseconds(10));
                buttonCancelBackup.IsEnabled = true;
            }
        }

        private void buttonCancelBackup_Click(object sender, RoutedEventArgs e)
        {
            m_cts.Cancel();
            buttonCancelBackup.IsEnabled = false;
        }

        private async void buttonSelectTempFolder_Click(object sender, RoutedEventArgs e)
        {
            FolderPicker folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            folderPicker.FileTypeFilter.Add("*");
            StorageFolder folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                textBlockMapsTempFolder.Text += folder.Path;
                do
                {
                    p.TempFolder = await folder.TryGetItemAsync("TempFolder") as StorageFolder;

                    // Try to create a temp to verify if symbolic link is correct, which may change if certificate changed.
                    try
                    {
                        IStorageFile items = await p.TempFolder.CreateFileAsync("Temp.txt");
                    }
                    catch (Exception)
                    {
                        var Msg = new Windows.UI.Popups.MessageDialog("Please verify symbolic link of the temp folder, make sure it's pointing to correct location.", "Invalid Temp Folder");
                        await Msg.ShowAsync();
                    }
                    finally
                    {
                        IStorageItem tempItem = await p.TempFolder.TryGetItemAsync("Temp.txt");
                        tempItem?.DeleteAsync();
                        p.TempFolder = null;
                    }
                    
                    if (p.TempFolder == null)
                    {
                        textBoxMklink.Text = string.Format("mklink /D {0}\\TempFolder {1}", folder.Path, ApplicationData.Current.TemporaryFolder.Path);
                        var result = await TempFolderDialog.ShowAsync();
                    }
                } while (p.TempFolder == null);
            }
        }

        private async void buttonBackupBuild_Click(object sender, RoutedEventArgs e)
        {
            FolderPicker folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            folderPicker.FileTypeFilter.Add("*");
            StorageFolder folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                m_backupFolders.Add("0B8j6UJY_E28CfkpjUnJ0NGUxcFZmVHVTNkhXZFg2TmF1REpPc2E4WEQ4OVBsZVc1V1RlQjg", folder);
                buttonStartBackup.IsEnabled = true;
                textBlockMapsBuild.Text += folder.Path;
            }
        }

        private async void buttonBackupBuildLogs_Click(object sender, RoutedEventArgs e)
        {
            FolderPicker folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            folderPicker.FileTypeFilter.Add("*");
            StorageFolder folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                m_backupFolders.Add("0B8j6UJY_E28CdGg5bXV2dGQtZzQ", folder);
                buttonStartBackup.IsEnabled = true;
                textBlockMapsBuildLogs.Text += folder.Path;

                StorageFolder binaryDepotFolder = await folder.GetFolderAsync("binarydepot");
                m_backupFolders.Add("0B8j6UJY_E28Cfk9GNWlxVDF4NGkxOGFmNFpsQlJIV0JFVWxMUVFIX3F2bXVrOW9ldmVxU28", binaryDepotFolder);
                buttonStartBackup.IsEnabled = true;
                textBlockMapsBinaryDepot.Text += binaryDepotFolder.Path;
            }
        }

        private async void buttonBackupBinaryDepot_Click(object sender, RoutedEventArgs e)
        {
            FolderPicker folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            folderPicker.FileTypeFilter.Add("*");
            StorageFolder folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                m_backupFolders.Add("0B8j6UJY_E28Cfk9GNWlxVDF4NGkxOGFmNFpsQlJIV0JFVWxMUVFIX3F2bXVrOW9ldmVxU28", folder);
                buttonStartBackup.IsEnabled = true;
                textBlockMapsBinaryDepot.Text += folder.Path;
            }
        }

        private async void buttonBackupComponentSDK_Click(object sender, RoutedEventArgs e)
        {
            FolderPicker folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
            folderPicker.FileTypeFilter.Add("*");
            StorageFolder folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                m_backupFolders.Add("0B8j6UJY_E28CWnJ2MEFqYkgxbGc", folder);
                buttonStartBackup.IsEnabled = true;
                textBlockMapsComponentSDK.Text += folder.Path;
            }
        }

        private void textStatus_TextChanged(object sender, TextChangedEventArgs e)
        {
            ScrolltoBottom((TextBox)sender);
        }

        private void ScrolltoBottom(TextBox textBox)
        {
            var grid = (Grid)VisualTreeHelper.GetChild(textBox, 0);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(grid); i++)
            {
                object obj = VisualTreeHelper.GetChild(grid, i);
                if (!(obj is ScrollViewer)) continue;
                ((ScrollViewer)obj).ChangeView(0.0f, ((ScrollViewer)obj).ExtentHeight, 1.0f);
                break;
            }
        }

        private void listViewStatus_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            listViewStatus.ScrollIntoView(listViewStatus.SelectedItem);
        }
    }

    public class LongToMegabytesStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            double bytes = System.Convert.ToDouble(value);
            string formatString = string.Format("{0}: {1:N2} MB", parameter as string, bytes / 1024 / 1024);

            return formatString;
        }

        // No need to implement converting back on a one-way binding 
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
