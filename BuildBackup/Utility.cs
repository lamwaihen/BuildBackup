using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
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

        public int DaysToDelete
        {
            get { return _daysToDelete; }
            set { _daysToDelete = value; OnPropertyChanged(); }
        }
        private int _daysToDelete = 180;

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
