using Google.Apis.Drive.v3.Data;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Core;

namespace BuildBackup
{
    public class Payload : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged = delegate { };

        public string FoundItem
        {
            get { return _foundItem; }
            set { _foundItem = string.Format("[{0}] {1}", DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss.fff"), value); OnPropertyChanged(); }
        }
        private string _foundItem = "";

        public string ProcessingItem
        {
            get { return _processingItem; }
            set { _processingItem = string.Format("[{0}] {1}", DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss.fff"), value); Log += _processingItem + "\r\n"; OnPropertyChanged(); }
        }
        private string _processingItem = "";

        public string Log
        {
            get { return _log; }
            set { _log = value; OnPropertyChanged(); }
        }
        private string _log = "";

        public int LoopInterval
        {
            get { return _loopInterval; }
            set { _loopInterval = value;OnPropertyChanged(); }
        }
        private int _loopInterval = 5;

        public int DaysToDelete
        {
            get { return _daysToDelete; }
            set { _daysToDelete = value; OnPropertyChanged(); }
        }
        private int _daysToDelete = 180;

        public int LatestLogID
        {
            get { return _latestLogID; }
            set { _latestLogID = value; OnPropertyChanged(); }
        }
        private int _latestLogID = 0;

        public string LastUpdateTime
        {
            get { return _lastUpdateTime; }
            set { _lastUpdateTime = value; OnPropertyChanged(); }
        }
        private string _lastUpdateTime = DateTime.Now.ToString();

        public int FolderMaxItems
        {
            get { return _folderMaxItems; }
            set { _folderMaxItems = value; OnPropertyChanged(); }
        }
        private int _folderMaxItems = 10000;

        public double FoundItemProgress
        {
            get { return _foundItemProgress; }
            set { _foundItemProgress = value; OnPropertyChanged(); }
        }
        private double _foundItemProgress = 0;

        public double ProcessingItemProgress
        {
            get { return _ProcessingItemProgress; }
            set { _ProcessingItemProgress = value; OnPropertyChanged(); }
        }
        private double _ProcessingItemProgress = 0;

        public bool? CanDeleteOldFiles
        {
            get { return _canDeleteOldFiles; }
            set { _canDeleteOldFiles = value; OnPropertyChanged(); }
        }
        private bool? _canDeleteOldFiles = true;

        public StorageFolder TempFolder
        {
            get { return _tempFolder; }
            set { _tempFolder = value; OnPropertyChanged(); }
        }
        private StorageFolder _tempFolder = null;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChangedEventHandler eventHandler = PropertyChanged;
            if (eventHandler != null)
            {
                Utility.UIThreadExecute(() => { eventHandler(this, new PropertyChangedEventArgs(propertyName)); });
            }
        }
    }
    public class SyncItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged = delegate { };

        public IStorageItem BackupSource
        {
            get { return _backupSource; }
            set
            {
                _backupSource = value;
                if (_backupSource != null)
                    _localPath = _backupSource.Path;
                OnPropertyChanged();
                OnPropertyChanged("LocalPath");
            }
        }
        private IStorageItem _backupSource = null;

        public File UploadDestination
        {
            get { return _uploadDestination; }
            set
            {
                _uploadDestination = value;
                _link = _uploadDestination != null ? string.Format("https://drive.google.com/open?id={0}", _uploadDestination.Id) : "";
                OnPropertyChanged();
                OnPropertyChanged("Link");
            }
        }
        private File _uploadDestination = null;

        /// <summary>
        /// Store the unzipped file for logs
        /// </summary>
        public IStorageItem ExtractedSource
        {
            get { return _extractedSource; }
            set { _extractedSource = value; OnPropertyChanged(); }
        }
        private IStorageItem _extractedSource = null;

        public string Name
        {
            get { return _name; }
            set { _name = value; OnPropertyChanged(); }
        }
        private string _name = "";

        public long Size
        {
            get { return _size; }
            set { _size = value; OnPropertyChanged(); }
        }
        private long _size = 0;

        public string Link
        {
            get { return _link; }
        }
        private string _link = "";


        public string LocalPath
        {
            get { return _localPath; }
        }
        private string _localPath = "";


        public string Status
        {
            get { return _status; }
            set
            {
                _status += string.Format("[{0}] {1}\n", DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss.fff"), value);
                OnPropertyChanged();
            }
        }
        private string _status = "";

        public long Received
        {
            get { return _received; }
            set { _received = value; OnPropertyChanged(); }
        }
        private long _received = 0;

        public long Sent
        {
            get { return _sent; }
            set { _sent = value; OnPropertyChanged(); }
        }
        private long _sent = 0;

        public double Progress
        {
            get { return _progress; }
            set { _progress = value; OnPropertyChanged(); }
        }
        private double _progress = 0;

        public string ParentFolderId
        {
            get { return _parentFolderId; }
            set {_parentFolderId = value; OnPropertyChanged(); }
        }
        private string _parentFolderId = "";

        public bool CanDeleteLocal
        {
            get { return _canDeleteLocal; }
            set { _canDeleteLocal = value; OnPropertyChanged(); }
        }
        private bool _canDeleteLocal = true;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChangedEventHandler eventHandler = PropertyChanged;
            if (eventHandler != null)
            {
                Utility.UIThreadExecute(() => { eventHandler(this, new PropertyChangedEventArgs(propertyName)); });
            }
        }
    }

    class Utility
    {
        static public CoreDispatcher UIdispatcher = Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher;
        static public void UIThreadExecute(Action action)
        {
            if (UIdispatcher.HasThreadAccess)
                action();
            else
                InnerExecute(action).Wait();
        }
        static private async Task InnerExecute(Action action)
        {
            await UIdispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
        }
    }
}
