using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Windows.System.Threading;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Devices.Enumeration;
using Windows.Media.FaceAnalysis;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Blob;

// 空白ページのアイテム テンプレートについては、http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409 を参照してください

namespace SurveillanceCamera
{
    /// <summary>
    /// それ自体で使用できる空白ページまたはフレーム内に移動できる空白ページ。
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private MediaCapture capture;
        private bool isPreview = false;
        private bool isRecording = false;
        private FaceTracker faceTracker;
        private ThreadPoolTimer timer;
        private Timer recordingTimer;
        private SemaphoreSlim semaphore = new SemaphoreSlim(1);
        private StorageFile videoFile;
        private const string fileName = "video.mp4";
        private const string storageName = "{Storage Name}";
        private const string storageKey = "{Storage Key}";

        /// <summary>
        /// 
        /// </summary>
        public MainPage()
        {
            this.InitializeComponent();
            Loaded += MainPage_Loaded;
            Unloaded += MainPage_Unloaded;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (isPreview)
            {
                await capture.StopPreviewAsync();
            }

            if (isRecording)
            {
                await capture.StopRecordAsync();
            }
            capture.Dispose();
            capture = null;

        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            
            if (faceTracker == null)
            {
                faceTracker = await FaceTracker.CreateAsync();
            }

            //カメラの初期化
            await InitCameraAsync();
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        private async Task InitCameraAsync()
        {
            
            try
            {
                if (capture != null)
                {
                    if (isPreview)
                    {
                        await capture.StopPreviewAsync();
                        isPreview = false;
                    }

                    capture.Dispose();
                    capture = null;

                }

                //カメラの設定
                var captureInitSettings = new MediaCaptureInitializationSettings();
                captureInitSettings.VideoDeviceId = "";
                captureInitSettings.StreamingCaptureMode = StreamingCaptureMode.Video;

                var camera= await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

                if (camera.Count() == 0)
                {
                    Debug.WriteLine("No Cmaera");
                    return;
                }
                else if (camera.Count() == 1)
                {
                    captureInitSettings.VideoDeviceId = camera[0].Id;
                }
                else
                {
                    captureInitSettings.VideoDeviceId = camera[1].Id;
                }

                capture = new MediaCapture();
                await capture.InitializeAsync(captureInitSettings);

                //ビデオの設定
                VideoEncodingProperties vp = new VideoEncodingProperties();

                vp.Height = 240;
                vp.Width = 320;
                vp.Subtype = "NV12";

                await capture.VideoDeviceController.SetMediaStreamPropertiesAsync(MediaStreamType.VideoPreview, vp);

                preview.Source = capture;

                Debug.WriteLine("Camera Initialized");

                //プレビューの開始
                await startPreview();

                
            }
            catch(Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        private async Task startPreview()
        {
            Debug.WriteLine("Start Preview");

            //プレビュースタート
            await capture.StartPreviewAsync();
            isPreview = true;

            //顔検出用タイマーの開始
            if (timer == null)
            {
                timer = ThreadPoolTimer.CreatePeriodicTimer(new TimerElapsedHandler(CurrentVideoFrame), TimeSpan.FromMilliseconds(66));
            }
            
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="timer"></param>
        private async void CurrentVideoFrame(ThreadPoolTimer timer)
        {
            if (!semaphore.Wait(0))
            {
                return;
            }

            try
            {
                IList<DetectedFace> faces = null;
                const BitmapPixelFormat inputPixelFormat = BitmapPixelFormat.Nv12;

                using (VideoFrame previewFrame = new VideoFrame(inputPixelFormat, 320, 240))
                {
                    await capture.GetPreviewFrameAsync(previewFrame);

                    //顔検出実行
                    if (FaceDetector.IsBitmapPixelFormatSupported(previewFrame.SoftwareBitmap.BitmapPixelFormat))
                    {
                        faces = await faceTracker.ProcessNextFrameAsync(previewFrame);
                    }
                    else
                    {
                        throw new System.NotSupportedException("PixelFormat '" + inputPixelFormat.ToString() + "' is not supported by FaceDetector");
                    }

                    //顔が検出されたら録画スタート
                    if (faces.Count!=0)
                    {
                        Debug.WriteLine("Found Face");
                        await startRecoding();
                    }
                }
            }
            catch(Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        private async Task startRecoding()
        {
            if (isPreview)
            {
                
                timer.Cancel();
                timer = null;
                isPreview = false;
                await capture.StopPreviewAsync();

                //録画開始
                isRecording = true;

                videoFile = await KnownFolders.VideosLibrary.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

                MediaEncodingProfile profile = null;
                profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Qvga);

                await capture.StartRecordToStorageFileAsync(profile, videoFile);

                isRecording = true;

                Debug.WriteLine("Start Recording");

                //15秒後に録画停止
                recordingTimer = new Timer(stopRecording, null, 15000, 15000);               
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="state"></param>
        private async void stopRecording(object state)
        {
            if (isRecording)
            {
                recordingTimer.Dispose();
                await capture.StopRecordAsync();

                Debug.WriteLine("Stop Recording");

                //録画ファイルのアップロード
                await fileUpload();

                isRecording = false;

                Debug.WriteLine("Upload Finish");

                //顔検出再開
                await startPreview();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        private async Task fileUpload()
        {
            Debug.WriteLine("Start Upload");

            try
            {
                StorageCredentials sc = new StorageCredentials(storageName, storageKey);
                CloudStorageAccount sa = new CloudStorageAccount(sc, true);

                CloudBlobClient client = sa.CreateCloudBlobClient();
                CloudBlobContainer container = client.GetContainerReference("video");
                await container.CreateIfNotExistsAsync();

                string uploadFilename = "video" + DateTime.UtcNow.ToString("yyyyMMddmmss") + ".mp4";
                CloudBlockBlob blockBlob = container.GetBlockBlobReference(uploadFilename);

                StorageFile storageFile = await KnownFolders.VideosLibrary.GetFileAsync("video.mp4");
                await blockBlob.UploadFromFileAsync(storageFile);
            }
            catch(Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }

        }
    }
}
