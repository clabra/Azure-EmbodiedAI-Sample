using UnityEngine;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net;
using System;
using System.Threading.Tasks;
using System.Threading;
using System.IO;
using System.Xml.Linq;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AzureEmbodiedAISamples
{
    public class SampleManager : MonoBehaviour
    {
        public SampleMode Mode; // Cloud/Hybrid

        private SampleConfig SampleConfig;

        private SpeechConfig TTSConfig;
        private SpeechConfig STTConfig;
        private AudioConfig AudioMicrophoneConfig;
        private AudioConfig AudioSpeakerConfig; // SpeakAsyncSystem
        private AudioSource AudioSource; // SpeakAsyncUnity
        //private int SampleRate = 48000; // SpeakAsyncUnity // Cloud 48KHz
        private int SampleRate = 24000; // SpeakAsyncUnity // Edge 24KHz

        public SpeechSynthesizer SpeechSynthesizer { get; private set; }
        public SpeechRecognizer SpeechRecognizer { get; private set; }
        public int recognizerInitialSilenceTimeoutMs = 30000; // 30 secs
        public int recognizerSegmentationSilenceTimeoutMs = 1500; // 1.5 secs

        public bool useBodyTracking;
        public BackgroundData m_lastFrameData = new BackgroundData();
        private SkeletalTrackingProvider m_skeletalTrackingProvider;
        private bool bodyTrackingResult = false;

        private SynchronizationContext synchronizationContext;
        private CancellationTokenSource cancellationToken;

        private void Awake()
        {
            synchronizationContext = SynchronizationContext.Current;
        }

        private void OnEnable()
        {
            cancellationToken = new CancellationTokenSource();
        }

        void Start()
        {
            string filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Application.productName + ".auth");
            string jsonString = File.ReadAllText(filePath);
            SampleConfig = JsonUtility.FromJson<SampleConfig>(jsonString);

            TTSConfig = (Mode == SampleMode.Cloud) ?
                SpeechConfig.FromSubscription(SampleConfig.TTSSubscriptionKey, SampleConfig.TTSRegion) :
                SpeechConfig.FromHost(new Uri("ws://localhost:5002"));
            // Reference:
            // https://learn.microsoft.com/en-us/azure/cognitive-services/speech-service/rest-text-to-speech?tabs=streaming#audio-outputs
            //TTSConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Raw48Khz16BitMonoPcm); // Cloud 48KHz
            TTSConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Raw24Khz16BitMonoPcm); // Edge 24KHz
            TTSConfig.SpeechSynthesisVoiceName = "en-US-AriaNeural";
            //AudioSpeakerConfig = AudioConfig.FromDefaultSpeakerOutput(); // SpeakAsyncSystem
            //SpeechSynthesizer = new SpeechSynthesizer(TTSConfig, AudioSpeakerConfig); // SpeakAsyncSystem
            SpeechSynthesizer = new SpeechSynthesizer(TTSConfig, null); // SpeakAsyncUnity

            SpeechSynthesizer.SynthesisStarted += SpeechSynthesizer_SynthesisStarted;
            SpeechSynthesizer.Synthesizing += SpeechSynthesizer_Synthesizing;
            SpeechSynthesizer.SynthesisCompleted += SpeechSynthesizer_SynthesisCompleted;
            SpeechSynthesizer.SynthesisCanceled += SpeechSynthesizer_SynthesisCanceled;
            SpeechSynthesizer.VisemeReceived += SpeechSynthesizer_VisemeReceived;
            SpeechSynthesizer.BookmarkReached += SpeechSynthesizer_BookmarkReached;

            STTConfig = (Mode == SampleMode.Cloud) ?
                SpeechConfig.FromSubscription(SampleConfig.STTSubscriptionKey, SampleConfig.STTRegion) :
                SpeechConfig.FromHost(new Uri("ws://localhost:5001"));
            STTConfig.SetProperty(PropertyId.SpeechServiceConnection_InitialSilenceTimeoutMs, recognizerInitialSilenceTimeoutMs.ToString());
            STTConfig.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, recognizerSegmentationSilenceTimeoutMs.ToString());
            AudioMicrophoneConfig = AudioConfig.FromDefaultMicrophoneInput();
            SpeechRecognizer = new SpeechRecognizer(STTConfig, AudioMicrophoneConfig);

            SpeechRecognizer.SpeechStartDetected += SpeechRecognizer_SpeechStartDetected;
            SpeechRecognizer.Recognized += SpeechRecognizer_Recognized;
            SpeechRecognizer.SpeechEndDetected += SpeechRecognizer_SpeechEndDetected;

            if (useBodyTracking)
            {
                const int TRACKER_ID = 0;
                m_skeletalTrackingProvider = new SkeletalTrackingProvider(TRACKER_ID);
            }

            AudioSource = GetComponent<AudioSource>(); // SpeakAsyncUnity
        }

        void Update()
        {
            UpdateBodyTracking();
        }

        #region Speech SDK
        private string GenerateSsml(string inputText)
        {
            var ssml = new XDocument(
                new XElement("speak",
                    new XAttribute("version", "1.0"),
                    new XAttribute(XNamespace.Xml + "lang", "en-US"),
                    new XElement("voice",
                        new XAttribute(XNamespace.Xml + "lang", "en-US"),
                        new XAttribute(XNamespace.Xml + "gender", "Male"),
                        new XAttribute("name", "en-US-AriaNeural"),
                        new XAttribute("style", "hopeful"),
                        new XElement("prosody",
                            new XAttribute(XNamespace.Xml + "rate", "+10.00%"),
                            new XAttribute(XNamespace.Xml + "pitch", "-5.00%"),
                            new XAttribute(XNamespace.Xml + "volume", "+5.00%"),
                            new XAttribute(XNamespace.Xml + "contour", "(5%, -61%) (48%, -6%)"),
                            inputText))));

            return ssml.ToString();
        }

        public async Task<bool> SpeakAsyncSystem(string inputText)
        {
            await SpeechSynthesizer.SpeakSsmlAsync(GenerateSsml(inputText)).ConfigureAwait(false);
            return true;
        }

        public async Task<bool> SpeakAsyncUnity(string inputText)
        { 
            // Reference:
            // https://github.com/Azure-Samples/cognitive-services-speech-sdk/blob/master/quickstart/csharp/unity/text-to-speech/Assets/Scripts/HelloWorld.cs

            var result = await SpeechSynthesizer.SpeakSsmlAsync(GenerateSsml(inputText));

            // Native playback is not supported on Unity yet (currently only supported on Windows/Linux Desktop).
            // Use the Unity API to play audio here as a short term solution.
            // Native playback support will be added in the future release.
            var audioDataStream = AudioDataStream.FromResult(result);
            var isFirstAudioChunk = true;
            var audioClip = AudioClip.Create(
                "Speech",
                SampleRate * 600, // Can speak 10mins audio as maximum
                1,
                SampleRate,
                true,
                (float[] audioChunk) =>
                {
                    var chunkSize = audioChunk.Length;
                    var audioChunkBytes = new byte[chunkSize * 2];
                    var readBytes = audioDataStream.ReadData(audioChunkBytes);
                    if (isFirstAudioChunk && readBytes > 0)
                    {
                        isFirstAudioChunk = false;
                    }

                    for (int i = 0; i < chunkSize; ++i)
                    {
                        if (i < readBytes / 2)
                        {
                            audioChunk[i] = (short)(audioChunkBytes[i * 2 + 1] << 8 | audioChunkBytes[i * 2]) / 32768.0F;
                        }
                        else
                        {
                            audioChunk[i] = 0.0f;
                        }
                    }

                    if (readBytes == 0)
                    {
                        synchronizationContext.Post(_ => { 
                            AudioSource.clip = null;  
                            AudioSource.Stop(); 
                        }, null);
                    }
                });

            AudioSource.clip = audioClip;
            AudioSource.Play();

            while (AudioSource.isPlaying)
            {
                await Task.Delay(100);
            }

            return true;
        }

        public async Task<string> ListenAsync()
        {
            var result = await SpeechRecognizer.RecognizeOnceAsync().ConfigureAwait(false);
            return result.Text;
        }
        #endregion

        #region BodyTracking SDK
        private void UpdateBodyTracking()
        {
            if (useBodyTracking && m_skeletalTrackingProvider != null && m_skeletalTrackingProvider.IsRunning)
            {
                if (m_skeletalTrackingProvider.GetCurrentFrameData(ref m_lastFrameData))
                {
                    bodyTrackingResult = m_lastFrameData.NumOfBodies > 0;
                }
            }
        }

        public async Task<bool> WatchAsync(float timeOut)
        {
            int timeLimit = (int)(timeOut * 1000);
            int timeElapsed = 0;
            int timeStep = 250;

            while (timeLimit > 0 && !bodyTrackingResult)
            {
                timeLimit -= timeStep;
                await Task.Delay(timeStep);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                timeElapsed += timeStep;
            }

            return bodyTrackingResult;
        }
        #endregion

        #region Purposeful WebAPIs
        public async Task<string> WebApiAsync(SampleWebApiType webApiType, string inputContent)
        {
            switch (webApiType)
            {
                case SampleWebApiType.CLU:
                    return await WebApiAsyncCLU(inputContent);
                case SampleWebApiType.GPT:
                    return await WebApiAsyncGPT(inputContent);
                case SampleWebApiType.KB:
                    return await WebApiAsyncKB(inputContent);
                default:
                    return await WebApiAsyncPostman(inputContent);
            }
        }

        private async Task<string> WebApiAsyncPostman(string inputContent)
        {
            var httpClient = new HttpClient();
            httpClient.BaseAddress = new Uri("https://postman-echo.com/");
            httpClient.DefaultRequestHeaders.Accept.Clear();
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var response = await httpClient.GetAsync("get").ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return string.Empty;
            }
            else
            {
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return json;
            }
        }

        private async Task<string> WebApiAsyncCLU(string inputContent)
        {
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", SampleConfig.CLUSubscriptionKey);

            var payload = new
            {
                kind = "Conversation",
                analysisInput = new
                {
                    conversationItem = new
                    {
                        id = "PARTICIPANT_ID_HERE",
                        text = inputContent,
                        modality = "text",
                        language = "en-US",
                        participantId = "PARTICIPANT_ID_HERE"
                    }
                },
                parameters = new
                {
                    projectName = "your_clu_project_name",
                    verbose = true,
                    deploymentName = "your_clu_deployment_name",
                    stringIndexType = "TextElement_V8"
                }
            };

            var json = JsonConvert.SerializeObject(payload);
            var data = new StringContent(json, Encoding.UTF8, "application/json");

            var url = SampleConfig.CLUURL;
            var response = await httpClient.PostAsync(url, data);
            var responseString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            return ParseCLUResponse(responseString);
        }

        private string ParseCLUResponse(string jsonInput)
        {
            try
            {
                JObject jsonObject = JObject.Parse(jsonInput);
                string topIntent = jsonObject["result"]["prediction"]["topIntent"].ToString();
                return topIntent;
            }
            catch (Exception ex)
            {
                Debug.Log(ex.Message);
                return string.Empty;
            }
        }

        private async Task<string> WebApiAsyncGPT(string inputContent)
        {
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("api-key", SampleConfig.GPTSubscriptionKey);

            var payload = new
            {
                prompt = inputContent,
                max_tokens = 100,
                temperature = 1,
                frequency_penalty = 0,
                presence_penalty = 0,
                top_p = 0.5,
                stop = (string)null
            };

            var json = JsonConvert.SerializeObject(payload);
            var data = new StringContent(json, Encoding.UTF8, "application/json");

            var url = SampleConfig.GPTURL;
            var response = await httpClient.PostAsync(url, data);
            var responseString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            return ParseGPTResponse(responseString);
        }

        private string ParseGPTResponse(string jsonInput)
        {
            try
            {
                JObject jsonObject = JObject.Parse(jsonInput);
                JArray choices = (JArray)jsonObject["choices"];
                string outputString = "";
                foreach (JObject choice in choices)
                {
                    outputString += choice["text"].ToString();
                }

                return outputString;
            }
            catch (Exception ex)
            {
                Debug.Log(ex.Message);
                return string.Empty;
            }
        }

        private async Task<string> WebApiAsyncKB(string inputContent)
        {
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("api-key", SampleConfig.KBSubscriptionKey);

            var url = SampleConfig.KBURL + inputContent;
            var response = await httpClient.GetAsync(url);
            var responseString = await response.Content.ReadAsStringAsync();

            return ParseKBResponse(responseString);
        }

        private string ParseKBResponse(string jsonInput)
        {
            try
            {
                JObject jsonObject = JObject.Parse(jsonInput);
                JArray value = (JArray)jsonObject["value"];
                return value[0]["Answer"].ToString();
            }
            catch (Exception ex)
            {
                Debug.Log(ex.Message);
                return string.Empty;
            }
        }
        #endregion

        #region Speech SDK event handlers
        private void SpeechRecognizer_SpeechStartDetected(object sender, RecognitionEventArgs e)
        {
            //TODO: Implement event handler as necessary
            //synchronizationContext.Post(_ => { Variables.Application.Set("SampleApplicationVariable", string.Empty); }, null);
        }

        private void SpeechRecognizer_Recognized(object sender, SpeechRecognitionEventArgs e)
        {
            //TODO: Implement event handler as necessary
            //synchronizationContext.Post(_ => { Variables.Application.Set("SampleApplicationVariable", string.Empty); }, null);
        }

        private void SpeechRecognizer_SpeechEndDetected(object sender, RecognitionEventArgs e)
        {
            //TODO: Implement event handler as necessary
            //synchronizationContext.Post(_ => { Variables.Application.Set("SampleApplicationVariable", string.Empty); }, null);
        }

        private void SpeechSynthesizer_SynthesisStarted(object sender, SpeechSynthesisEventArgs e)
        {
            //TODO: Implement event handler as necessary
            //synchronizationContext.Post(_ => { Variables.Application.Set("SampleApplicationVariable", string.Empty); }, null);
        }

        private void SpeechSynthesizer_Synthesizing(object sender, SpeechSynthesisEventArgs e)
        {
            //TODO: Implement event handler as necessary
            //synchronizationContext.Post(_ => { Variables.Application.Set("SampleApplicationVariable", string.Empty); }, null);
        }

        private void SpeechSynthesizer_SynthesisCompleted(object sender, SpeechSynthesisEventArgs e)
        {
            //TODO: Implement event handler as necessary
            //synchronizationContext.Post(_ => { Variables.Application.Set("SampleApplicationVariable", string.Empty); }, null);
        }

        private void SpeechSynthesizer_SynthesisCanceled(object sender, SpeechSynthesisEventArgs e)
        {
            //TODO: Implement event handler as necessary
            //synchronizationContext.Post(_ => { Variables.Application.Set("SampleApplicationVariable", string.Empty); }, null);
        }

        private void SpeechSynthesizer_VisemeReceived(object sender, SpeechSynthesisVisemeEventArgs e)
        {
            //TODO: Implement event handler as necessary
            //Debug.Log($"Viseme event received. Audio offset: " + $"{e.AudioOffset / 10000}ms, viseme id: {e.VisemeId}.");
        }

        private void SpeechSynthesizer_BookmarkReached(object sender, SpeechSynthesisBookmarkEventArgs e)
        {
            //TODO: Implement event handler as necessary
            //synchronizationContext.Post(_ => { Variables.Application.Set("SampleApplicationVariable", string.Empty); }, null);
        }
        #endregion

        void OnApplicationQuit()
        {
            cancellationToken.Cancel();
        }

        void OnDestroy()
        {
            if (SpeechSynthesizer != null)
            {
                SpeechSynthesizer.SynthesisStarted -= SpeechSynthesizer_SynthesisStarted;
                SpeechSynthesizer.Synthesizing -= SpeechSynthesizer_Synthesizing;
                SpeechSynthesizer.SynthesisCompleted -= SpeechSynthesizer_SynthesisCompleted;
                SpeechSynthesizer.SynthesisCanceled -= SpeechSynthesizer_SynthesisCanceled;

                SpeechSynthesizer.VisemeReceived -= SpeechSynthesizer_VisemeReceived;
                SpeechSynthesizer.BookmarkReached -= SpeechSynthesizer_BookmarkReached;

                SpeechSynthesizer.Dispose();
            }

            if (SpeechRecognizer != null)
            {
                SpeechRecognizer.SpeechStartDetected -= SpeechRecognizer_SpeechStartDetected;
                SpeechRecognizer.Recognized -= SpeechRecognizer_Recognized;
                SpeechRecognizer.SpeechEndDetected -= SpeechRecognizer_SpeechEndDetected;

                SpeechRecognizer.Dispose();
            }

            StopAllCoroutines();
        }
    }
}