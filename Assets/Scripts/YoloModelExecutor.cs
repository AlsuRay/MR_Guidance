using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using Unity.Sentis;
using UnityEngine;

namespace Assets.Scripts
{
    public class YoloModelExecutor : MonoBehaviour
    {
        [Header("Models")]
        public ModelAsset SimpleModelAsset;  // COCO — загружается по умолчанию
        public ModelAsset ChemLabModelAsset; // кастомная модель

        public Material ShaderForScaling;

        private TextureTransform textureTransform;
        private bool disposed;
        private IWorker worker;
        private TensorFloat inputTensor;
        private TensorFloat outputTensor;
        private ModelState modelState = ModelState.PreProcessing;
        private CameraTransform cameraTransform;
        private bool hasMoreModelToRun = true;
        private IEnumerator modelEnumerator;
        private RenderTexture intermediateRenderTexture;
        private YoloDebugOutput yoloDebugOutput;
        private int layerCount;
        private float threshold;
        private YoloRecognitionHandler yoloRecognitionHandler;

        private static WebCamTextureAccess WebCamTextureAccess => WebCamTextureAccess.Instance;
        private static SettingsProvider SettingsProvider => SettingsProvider.Instance;

        private ByteTrackClient _byteTrack;

        public void SwitchModel(bool isChemLab)
        {
            this.worker?.Dispose();
            this.inputTensor?.Dispose();
            this.inputTensor = null;

            ModelAsset asset = isChemLab ? ChemLabModelAsset : SimpleModelAsset;
            Model model = ModelLoader.Load(asset);
            this.worker = WorkerFactory.CreateWorker(BackendType.GPUCompute, model);

            this.modelState        = ModelState.PreProcessing;
            this.modelEnumerator   = null;
            this.hasMoreModelToRun = true;
            Debug.Log($"[YoloModelExecutor] Switched to {(isChemLab ? "ChemLab" : "Simple")} model");
        }

        private void Start()
        {
            this.yoloDebugOutput        = gameObject.GetComponent<YoloDebugOutput>();
            this.yoloRecognitionHandler = gameObject.GetComponent<YoloRecognitionHandler>();

            this.SettingsProviderOnPropertyChanged(null, new PropertyChangedEventArgs(nameof(SettingsProvider.ModelExecutionOR)));
            this.SettingsProviderOnPropertyChanged(null, new PropertyChangedEventArgs(nameof(SettingsProvider.ThresholdOR)));
            SettingsProvider.PropertyChanged += this.SettingsProviderOnPropertyChanged;

            // Load Simple model by default
            Model model = ModelLoader.Load(this.SimpleModelAsset);
            this.worker = WorkerFactory.CreateWorker(BackendType.GPUCompute, model);

            WebCamTextureAccess.Play();
            _byteTrack = gameObject.AddComponent<ByteTrackClient>();
            this.intermediateRenderTexture = new RenderTexture(
                Parameters.ModelImageResolution.x, Parameters.ModelImageResolution.y, 24);
            this.ShaderForScaling.SetFloat("_Aspect",
                (float)WebCamTextureAccess.ActualCameraSize.x / WebCamTextureAccess.ActualCameraSize.y
                * Parameters.ModelImageResolution.y / Parameters.ModelImageResolution.x);
            this.textureTransform = new TextureTransform().SetDimensions(
                Parameters.ModelImageResolution.x, Parameters.ModelImageResolution.y, 3);
        }

        private void Update()
        {
            switch (this.modelState)
            {
                case ModelState.Idle:
                    break;
                case ModelState.PreProcessing:
                    this.inputTensor?.Dispose();
                    this.cameraTransform = new CameraTransform(Camera.main);
                    Graphics.Blit(WebCamTextureAccess.WebCamTexture,
                                  this.intermediateRenderTexture, this.ShaderForScaling);
                    this.inputTensor = TextureConverter.ToTensor(
                        this.intermediateRenderTexture, this.textureTransform);
                    this.modelState = ModelState.Executing;
                    break;
                case ModelState.Executing:
                    this.modelEnumerator ??= this.worker.StartManualSchedule(this.inputTensor);
                    int i = 0;
                    while (i++ < this.layerCount && this.hasMoreModelToRun)
                        this.hasMoreModelToRun = this.modelEnumerator.MoveNext();
                    if (!this.hasMoreModelToRun)
                    {
                        this.modelEnumerator   = null;
                        this.hasMoreModelToRun = true;
                        this.modelState        = ModelState.ReadOutput;
                    }
                    break;
                case ModelState.ReadOutput:
                    this.outputTensor = (TensorFloat)this.worker.PeekOutput();
                    this.modelState   = ModelState.Idle;
                    this.outputTensor.AsyncReadbackRequest(_ => this.modelState = ModelState.PostProcessing);
                    break;
                case ModelState.PostProcessing:
                    this.outputTensor.MakeReadable();
                    List<YoloItem> result = YoloModelOutputProcessor.ProcessModelOutput(
                        this.outputTensor, this.threshold);
                    this.yoloDebugOutput.ShowDebugInformation(this.inputTensor, result, this.cameraTransform);
                    yoloRecognitionHandler.ShowRecognitions(result, this.cameraTransform);
                    _byteTrack.SendDetections(result, this.cameraTransform,
                        new Vector2(Parameters.ModelImageResolution.x, Parameters.ModelImageResolution.y));
                    this.modelState = ModelState.PreProcessing;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        public void OnDestroy()
        {
            if (this.disposed) return;
            this.disposed = true;
            SettingsProvider.PropertyChanged -= this.SettingsProviderOnPropertyChanged;
            WebCamTextureAccess.Stop();
            this.inputTensor?.Dispose();
            this.worker?.Dispose();
        }

        private void SettingsProviderOnPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(SettingsProvider.ModelExecutionOR): this.UpdateModelPerformance(); break;
                case nameof(SettingsProvider.ThresholdOR):      this.UpdateThreshold();        break;
            }
        }

        private void UpdateModelPerformance()
        {
            this.layerCount = SettingsProvider.ModelExecutionOR switch
            {
                ModelExecutionMode.High => Parameters.LayersHigh,
                ModelExecutionMode.Low  => Parameters.LayersLow,
                ModelExecutionMode.Full => int.MaxValue,
                _ => this.layerCount
            };
        }

        private void UpdateThreshold()
        {
            this.threshold = SettingsProvider.ThresholdOR switch
            {
                RecognitionThreshold.High   => Parameters.ThresholdHigh,
                RecognitionThreshold.Medium => Parameters.ThresholdMedium,
                RecognitionThreshold.Low    => Parameters.ThresholdLow,
                _ => this.threshold
            };
        }
    }
}