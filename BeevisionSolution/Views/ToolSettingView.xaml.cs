using BeevisionSolution.Controller;
using BeevisionSolution.Jobs;
using BeevisionSolution.Models;
using BeevisionSolution.ViewComponents;
using BeevisionSolution.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using static BeevisionSolution.Utils.Constant;
using System.Threading.Tasks;

namespace BeevisionSolution.Views
{
    /// <summary>
    /// Interaction logic for ToolSettingView.xaml
    /// </summary>
    public partial class ToolSettingView : UserControl, IDisposable
    {
        ToolBlockEditorView wCamera = new ToolBlockEditorView();
        ToolBlockEditorView wHeTbl = new ToolBlockEditorView();
        private bool _isLoadingJob;

        public ToolSettingView()
        {
            InitializeComponent();
            wCamera.OnHitRunButtonEvent += OnGrabImage;
            
            var mainWindow = (Application.Current.MainWindow as MainWindow2);
            if (mainWindow != null)
                mainWindow.OnAllJobLoadedDone += OnJobLoadedDone;
            Init();
        }

        private void OnJobLoadedDone(object sender)
        {
            Init();
        }

        private void Init()
        {
            var lst = JobController.GetAllJobs(false);
            if (lst.Count <= 0)
            {
                return;
            }

            var lstToolJobs = new List<BaseJob>();
            for (int i = 0; i < lst.Count; i++)
            {
                if (lst[i] is AlignJob || lst[i] is IspJob || lst[i] is WatcherJob)
                {
                    lstToolJobs.Add(lst[i]);
                }
            }

            cbxJobs.ItemsSource = lstToolJobs;
            if ((null != lstToolJobs) && (lstToolJobs.Count > 0))
            {
                cbxJobs.SelectedIndex = 0;
            }
        }

        private void OnGrabImage(object image)
        {
            if (wHeTbl != null)
            {
                wHeTbl.InputImage = image;
            }
        }
        private async Task LoadJobForSelectedItem()
        {
            if (_isLoadingJob || cbxJobs.Items.Count < 1 || cbxJobs.SelectedIndex < 0)
                return;

            var job = (BaseJob)cbxJobs.SelectedItem;
            if (job == null)
                return;

            _isLoadingJob = true;
            cbxJobs.IsEnabled = false;
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                if (!job.Initialized)
                    job.Init();

                var img = (Object)null;
                var camJob = JobController.GetCameraJob(job.CamSettings.CameraId);

                if (null != camJob)
                {
                    if ((null == camJob.OutputImage) && (null == camJob.LastValidImage))
                    {
                        bool captureSucceeded = await camJob.RunToolAsync();
                        if (!captureSucceeded)
                        {
                            Common.Bug("[ToolSettingView] Camera capture failed while loading job: {0}", camJob.Name);
                        }
                        img = camJob.OutputImage;
                    }
                    else if (null != camJob.LastValidImage)
                    {
                        img = camJob.LastValidImage;
                    }
                    else
                    {
                        img = camJob.OutputImage;
                    }
                }

                await wCamera.SetJobAsync(camJob, null);

                if (!panelLeft.Children.Contains(wCamera))
                {
                    panelLeft.Children.Clear();
                    panelLeft.Children.Add(wCamera);
                }

                await wHeTbl.SetJobAsync(job, img);

                if (!panelRight.Children.Contains(wHeTbl))
                {
                    panelRight.Children.Clear();
                    panelRight.Children.Add(wHeTbl);
                }
            }
            catch (Exception ex)
            {
                Common.Bug("[ToolSettingView] Failed to load selected job: {0}", ex.Message);
                Common.Bug(ex.StackTrace);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                cbxJobs.IsEnabled = true;
                _isLoadingJob = false;
            }
        }

        private async void cbxJobs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await LoadJobForSelectedItem();
        }

        private void btnTest_Click(object sender, RoutedEventArgs e)
        {
            var job = (FunctionJob)cbxJobs.SelectedItem;
            OpCallWindow w = new OpCallWindow();
            w.SetJob(job);
            w.ShowDialog();
        }

        private void btnSaveOffset_Click(object sender, RoutedEventArgs e)
        {
        }

        public void Dispose()
        {
            var mainWindow = (Application.Current.MainWindow as MainWindow2);
            if (mainWindow != null)
            {
                mainWindow.OnAllJobLoadedDone -= OnJobLoadedDone;
            }

            if (wCamera != null)
            {
                wCamera.OnHitRunButtonEvent -= OnGrabImage;
                wCamera.Dispose();
                wCamera = null;
            }

            if (wHeTbl != null)
            {
                wHeTbl.Dispose();
                wHeTbl = null;
            }
        }
    }
}
