﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using System.Windows.Input;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Core;
using BABERECEN.Client.Helpers;
using BABERECEN.Client.Models;
using Microsoft.Bot.Connector.DirectLine;
using Newtonsoft.Json;
using Reactive.Bindings.Extensions;
using WebSocketSharp;
using BABERECEN.Client.Core.Helpers;
using Amazon.Polly;
using Amazon;
using Amazon.Polly.Model;
using System.Net;

namespace BABERECEN.Client.ViewModels
{
    /// <summary>
    ///     메인 페이지 뷰모델
    /// </summary>
    public class MainViewModel : BindableBase
    {
        /// <summary>
        ///     다이렉트 라인 시크릿
        /// </summary>
        private const string DIRECT_LINE_SECRET = "dCSsF48Sils.DYraGktYtKxbzv1A3lHr-nx6GrmlRt-47f8-8hGzCcI";
        private const string SRGS_FILE_NAME = "SRGS.xml";

        private readonly string _botId = "BABERECEN";
        private readonly string _fromUser = "BABE104";

        private readonly CompositeDisposable _compositeDisposable = new CompositeDisposable();

        public static string requestUri = "https://westus.stt.speech.microsoft.com/speech/recognition/conversation/cognitiveservices/v1?language=en-US";
        public static string subscriptionKey = "782a715164bd4d579e038746631e16a0";

        /// <summary>
        ///     클라이언트 상태를 주제로 하는 서브젝트
        /// </summary>
        private readonly Subject<ClientState> _subject = new Subject<ClientState>();

        /// <summary>
        ///     봇클라이언트
        /// </summary>
        private DirectLineClient _botClient;

        /// <summary>
        ///     컨버세이션
        /// </summary>
        private Conversation _conversation;

        private ClientStates _currentClientState;

        private string _inputText;
        private bool _isConversation;
        private int _retryCount;
        private SpeechRecognizer _speechRecognizer;
        private string _watermark;

        private WebSocket _webSocketClient;
        private bool _isRecoding;
        private string _recodingFileName;
        private IObservable<long> _threeSecondsObservable;
        private IDisposable _threeSecondDisposer;
        private bool _isSpeechDetected;
        private IRandomAccessStream _randomAccessStream;

        /// <summary>
        ///     생성자
        /// </summary>
        public MainViewModel()
        {
            //SpeechHelper speechHelper= new SpeechHelper();
            //_speechHelper = speechHelper ?? throw new ArgumentNullException(nameof(speechHelper));

            if (!DesignMode.DesignModeEnabled) Init();            
        }

        /// <summary>
        ///     전송 버튼 커맨드
        /// </summary>
        public ICommand SendCommand { get; set; }

        /// <summary>
        ///     다이얼로그 목록
        /// </summary>
        public IList<Activity> Dialogs { get; set; }

        /// <summary>
        ///     입력받은 텍스트
        /// </summary>
        public string InputText
        {
            get => _inputText;
            set => Set(ref _inputText, value);
        }

        public ClientStates CurrentClientState
        {
            get => _currentClientState;
            set => Set(ref _currentClientState, value);
        }

        /// <summary>
        ///     초기화
        /// </summary>
        private async void Init()
        {
            Dialogs = new ObservableCollection<Activity>();
            SendCommand = new RelayCommand(ExecuteSendCommand);
            MediaEndedCommand = new RelayCommand(ExecuteMediaEndedCommand);

            //봇 컨버세이션 시작
            await StartBotConversationAsync();

            //음성 인식 초기화 - 한글을 지원하지 않기 때문에 영문으로 인식하도록 함
            var supportedLanguages = SpeechRecognizer.SupportedGrammarLanguages;
            var enUS = supportedLanguages.FirstOrDefault(p => p.LanguageTag == "en-US")
                       ?? SpeechRecognizer.SystemSpeechLanguage; 
            await InitializeRecognizerAsync(enUS);

            _threeSecondsObservable = System.Reactive.Linq.Observable.Timer(TimeSpan.FromSeconds(3));

            //상태 변경 이벤트 옵저블
            var stateChangingObservable =
                System.Reactive.Linq.Observable.FromEvent<EventHandler<ClientState>, ClientState>((Action<ClientState> h) => (object s, ClientState a) => h(a),
                    (EventHandler<ClientState> h) => ClientStateChanging += h, (EventHandler<ClientState> h) => ClientStateChanging -= h);

            stateChangingObservable
                .Subscribe(_subject)
                .AddTo(_compositeDisposable);

            _subject
                .Subscribe(async state =>
                {
                    await ExecuteDispatcherRunAsync(() =>
                    {
                        //클라이언트의 상태를 일단 변경
                        CurrentClientState = state.States;
                    });

                    Debug.WriteLine(state.States);
                    switch (state.States)
                    {
                        case ClientStates.Idle:
                            Debug.WriteLine($"Idle {DateTime.Now:O}");
                            break;
                        case ClientStates.StartConversation:
                            var args = (SpeechContinuousRecognitionResultGeneratedEventArgs)state.Data;
                            var action =
                                args.Result.SemanticInterpretation.Properties
                                    .FirstOrDefault(p => p.Key == "ACTION");
                            if (string.IsNullOrEmpty(action.Key)) return;

                            var command = action.Value.FirstOrDefault();
                            switch (command)
                            {
                                case "BEGIN":
                                    //시스템 메시지 출력
                                    await ExecuteDispatcherRunAsync(async () =>
                                    {
                                        Dialogs.Add(new Activity
                                        {
                                            Text = $"[System Message] Start Conversation"
                                        });
                                        await SendMessageToBotAsync("Start");
                                        //PlaySystemMessage("hello.mp3");
                                    });
                                    _isConversation = true;
                                    _retryCount = 0;
                                    break;
                            }

                            break;
                        case ClientStates.PlaySystemVoice:
                            Debug.WriteLine($"PlaySystemVoice {DateTime.Now:O}");

                            break;
                        case ClientStates.StopSystemVoice:
                            Debug.WriteLine($"StopSystemVoice {DateTime.Now:O}");
                            Dialogs.Add(new Activity()
                            {
                                Text = "[System Message] Speak Please"
                            });
                            RecodingVoice();
                            break;
                        case ClientStates.StartRecoding:
                            Debug.WriteLine($"StartRecoding {DateTime.Now:O} {state.Data}");
                            _isSpeechDetected = false;
                            break;
                        case ClientStates.StopRecoding:
                            Debug.WriteLine($"EndRecoding {DateTime.Now:O} {state.Data}");
                            //구독해지
                            _threeSecondDisposer.Dispose();

                            //음성 딕텍트가 되었을 경우에만 전송한다.
                            if (_isSpeechDetected)
                            {
                                await ExecuteDispatcherRunAsync(async () =>
                                {
                                    await SendMessageToBotAsync("voice command", state.Data.ToString());
                                });
                            }
                            else
                            {
                                await Singleton<MicrophoneHelper>.Instance.RemoveRecordingAsync(state.Data.ToString());
                                //음성 입력이 않되었다면 다시 한번 이야기 해주세요~
                                //시스템 메시지 출력
                                await ExecuteDispatcherRunAsync(() =>
                                {
                                    //PlaySystemMessage("again1.mp3");
                                });
                            }
                            _isSpeechDetected = false;

                            break;
                        case ClientStates.SendVoiceCommand:
                            break;
                        case ClientStates.ReceiveVoiceCommandResult:
                            break;
                        case ClientStates.PlayVoiceCommandResult:
                            break;
                        case ClientStates.StopVoiceCommandResult:
                            break;
                        case ClientStates.EndConversation:
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                });
        }

        private void ExecuteMediaEndedCommand()
        {
            switch (CurrentClientState)
            {
                case ClientStates.PlaySystemVoice:
                    OnClientStateChanging(ClientStates.StopSystemVoice);
                    break;
                case ClientStates.PlayVoiceCommandResult:
                    OnClientStateChanging(ClientStates.StopVoiceCommandResult);
                    break;
            }
        }

        private async Task InitializeRecognizerAsync(Language language)
        {
            try
            {
                var grammarContentFile = await Package.Current.InstalledLocation.GetFileAsync(SRGS_FILE_NAME);
                if (grammarContentFile == null)
                    throw new NullReferenceException("SRGS 파일이 존재하지 않습니다.");

                // Create an instance of SpeechRecognizer.
                _speechRecognizer = new SpeechRecognizer(language);

                //마이크 입력 인지
                var stateChangedObservable = System.Reactive.Linq.Observable
                    .FromEvent<TypedEventHandler<SpeechRecognizer, SpeechRecognizerStateChangedEventArgs>,
                        SpeechRecognizerStateChangedEventArgs>(
                        (Action<SpeechRecognizerStateChangedEventArgs> h) => (SpeechRecognizer sender, SpeechRecognizerStateChangedEventArgs args) => h(args),
                        (TypedEventHandler<SpeechRecognizer, SpeechRecognizerStateChangedEventArgs> h) => _speechRecognizer.StateChanged += h,
                        (TypedEventHandler<SpeechRecognizer, SpeechRecognizerStateChangedEventArgs> h) => _speechRecognizer.StateChanged -= h);

                stateChangedObservable.Subscribe(s =>
                {
                    switch (s.State)
                    {
                        case SpeechRecognizerState.SoundStarted:
                            break;
                        case SpeechRecognizerState.SoundEnded:
                            break;
                        case SpeechRecognizerState.SpeechDetected:
                            //대화시작이 않되었거나 레코딩 중이 아니라면 음성 디텍팅을 하지 않음
                            if (_isConversation == false || _isRecoding == false) return;
                            _isSpeechDetected = true;
                            Debug.WriteLine("SpeechDetected");
                            break;
                        default:
                            Debug.WriteLine($"OnNext : {s}");
                            break;
                    }
                },
                        ex => { Debug.WriteLine($"Error : {ex.Message}"); },
                        () => { Debug.WriteLine("OnCompleted"); })
                    .AddTo(_compositeDisposable);


                //윈도우 지원 음성 인식
                var resultGeneratedObservable =
                    System.Reactive.Linq.Observable
                        .FromEvent<TypedEventHandler<SpeechContinuousRecognitionSession,
                                SpeechContinuousRecognitionResultGeneratedEventArgs>,
                            SpeechContinuousRecognitionResultGeneratedEventArgs>(
                            (Action<SpeechContinuousRecognitionResultGeneratedEventArgs> h) => (SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionResultGeneratedEventArgs args) => h(args),
                            (TypedEventHandler<SpeechContinuousRecognitionSession, SpeechContinuousRecognitionResultGeneratedEventArgs> h) => _speechRecognizer.ContinuousRecognitionSession.ResultGenerated += h,
                            (TypedEventHandler<SpeechContinuousRecognitionSession, SpeechContinuousRecognitionResultGeneratedEventArgs> h) => _speechRecognizer.ContinuousRecognitionSession.ResultGenerated -= h);

                resultGeneratedObservable
                    .Where(args => !(args.Result.Confidence == SpeechRecognitionConfidence.Low
                                     || args.Result.Confidence == SpeechRecognitionConfidence.Rejected)
                                   && args.Result.SemanticInterpretation.Properties.Any(p => p.Key == "ACTION"))
                    .Select(args => new ClientState { States = ClientStates.StartConversation, Data = args })
                    .Subscribe(_subject)
                    .AddTo(_compositeDisposable);

                //SRGS 읽어서 조건에 추가
                var grammarConstraint = new SpeechRecognitionGrammarFileConstraint(grammarContentFile);
                _speechRecognizer.Constraints.Add(grammarConstraint);

                // Compile the constraint.
                var compilationResult = await _speechRecognizer.CompileConstraintsAsync();
                // Check to make sure that the constraints were in a proper format and the recognizer was able to compile it.
                if (compilationResult.Status != SpeechRecognitionResultStatus.Success)
                    Debug.WriteLine("Error SpeechRecognizer.CompileConstraints");

                //연속 음성 인식 시작 - 시작 단어 확인하기 위해
                await _speechRecognizer.ContinuousRecognitionSession.StartAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                throw;
            }
        }

        /// <summary>
        ///     봇 초기화
        /// </summary>
        /// <returns></returns>
        private async Task StartBotConversationAsync()
        {
            //다이렉트라인 클라이언트 생성
            _botClient = new DirectLineClient(DIRECT_LINE_SECRET);
            _conversation = await _botClient.Conversations.StartConversationAsync();

            Debug.WriteLine($"ConversationId : {_conversation.ConversationId}");

            //웹소켓 클라이언트
            _webSocketClient = new WebSocket(_conversation.StreamUrl);
            _webSocketClient.OnMessage += WebSocketClient_OnMessage;
            _webSocketClient.Connect();
        }

        /// <summary>
        ///     소켓 메시지 수신
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void WebSocketClient_OnMessage(object sender, MessageEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;

            var activitySet = JsonConvert.DeserializeObject<ActivitySet>(e.Data);
            //언젠가 쓰일지도 모르는..
            _watermark = activitySet.Watermark;

            //botId와 맞는 녀석만 골라내고..
            var activities = from x in activitySet.Activities
                             where x.From.Id == _botId
                             select x;


            //폴리 클라이언트 생성
            var pc = new AmazonPollyClient("AKIAI2VB7NBZUIALEPEA", "keKSzndXgNqMIG5CVXRImScjodyVgjRpf04B0zx9"
                , RegionEndpoint.APNortheast2);

            foreach (var activity in activities)
            {
                await ExecuteDispatcherRunAsync(async () =>
                {
                    //요청 생성
                    var sreq = new SynthesizeSpeechRequest
                    {
                        Text = $"<speak>{activity.Text}</speak>",
                        OutputFormat = OutputFormat.Mp3,
                        VoiceId = VoiceId.Joanna,
                        LanguageCode = "en-US",
                        TextType = TextType.Ssml
                    };

                    //e.Data = string.Empty;

                    //서비스 요청
                    var sres = await pc.SynthesizeSpeechAsync(sreq);

                    //서비스 요청 결과 확인
                    if (sres.HttpStatusCode != HttpStatusCode.OK)
                        return;

                    //파일명 생성
                    var fileName = $@"{ApplicationData.Current.LocalFolder.Path}\{DateTime.Now:yyMMddhhmmss}.mp3";
                    //파일에 AudioStream 쓰기
                    using (var fileStream = File.Create(fileName))
                    {
                        sres.AudioStream.CopyTo(fileStream);
                        fileStream.Flush();
                        fileStream.Close();
                    }
                    //생성된 파일을 가져오기
                    var file = await StorageFile.GetFileFromPathAsync(fileName);
                    //파일을 열어서 RandomAccessStream 프로퍼티에 입력
                    RandomAccessStream = await file.OpenAsync(FileAccessMode.Read);
                });
            }
            //RandomAccessStream과 바인딩이 되어있는 MediaBehavior에서 MediaPlayer를 통해서 재생

            foreach (var activity in activities) await ExecuteDispatcherRunAsync(() => Dialogs.Add(activity));

            OnClientStateChanging(ClientStates.PlaySystemVoice);
        }

        /// <summary>
        ///     전송 버튼 커맨드 실행
        /// </summary>
        private async void ExecuteSendCommand()
        {
            if (string.IsNullOrEmpty(InputText)) return;
            await SendMessageToBotAsync(InputText);
            InputText = string.Empty;
        }

        /// <summary>
        ///     메시지 송신
        /// </summary>
        /// <returns></returns>
        private async Task SendMessageToBotAsync(string message, string speechFileName = null)
        {
            Debug.WriteLine($"SendMessageToBotAsync : {message}");

            var userMessage = new Activity
            {
                From = new ChannelAccount(_fromUser),
                Text = message,
                Type = ActivityTypes.Message
            };

            //파일 이름이 존재하면 파일 전송
            if (string.IsNullOrEmpty(speechFileName) == false)
            {
                var filePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, speechFileName);
                var fileData = Convert.ToBase64String(File.ReadAllBytes(filePath));
                var attachment = new Attachment
                {
                    Name = speechFileName,
                    ContentType = "audio/wav",
                    ContentUrl = $"data:audio/wav;base64,{fileData}"
                };

                
                //음성파일 text로 변환
                string requestUrl = requestUri;
                HttpWebRequest request = (HttpWebRequest)HttpWebRequest.Create(requestUrl);
                request.SendChunked = true;
                request.Accept = @"application/json;text/xml";
                request.Method = "POST";
                request.ProtocolVersion = HttpVersion.Version11;
                request.ContentType = @"audio/wav;codec=audio/pcm;samplerate=16000";
                request.Headers["Ocp-Apim-Subscription-Key"] = subscriptionKey;
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    byte[] buffer = null;
                    int bytesRead = 0;
                    using (Stream requestStream = request.GetRequestStream())
                    {
                        buffer = new Byte[checked((uint)Math.Min(1024, (int)fs.Length))];
                        while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) != 0)
                        {
                            requestStream.Write(buffer, 0, bytesRead);
                        }

                        requestStream.Flush();
                    }
                }
                string re = null;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    using (StreamReader sr = new StreamReader(response.GetResponseStream()))
                    {
                        re = sr.ReadToEnd();
                    }
                }

                dynamic data = JsonConvert.DeserializeObject(re);

                userMessage.Attachments = new List<Attachment>
                {
                    attachment
                };
                userMessage.Text = data.DisplayText;
               

                Dialogs.Add(userMessage);
                await _botClient.Conversations.PostActivityAsync(_conversation.ConversationId, userMessage);
            }
            else
            {
                Dialogs.Add(userMessage);
                await _botClient.Conversations.PostActivityAsync(_conversation.ConversationId, userMessage);
            }

        }

        /// <summary>
        ///     디스패처 실행
        /// </summary>
        /// <param name="handler"></param>
        /// <returns></returns>
        private async Task ExecuteDispatcherRunAsync(DispatchedHandler handler)
        {
            await CoreApplication.MainView.Dispatcher
                .RunAsync(CoreDispatcherPriority.Normal, handler);
        }

        private async void RecodingVoice()
        {
            //3초간 녹음
            //대화시작이 않되었거나 레코딩 중이라면 새로운 레코딩 시작을 하지 않음
            if (_isConversation == false || _isRecoding) return;
            _isRecoding = true;
            _recodingFileName = "voice_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".wav";
            await Singleton<MicrophoneHelper>.Instance.StartRecordingAsync(_recodingFileName);
            OnClientStateChanging(ClientStates.StartRecoding, _recodingFileName);

            _threeSecondDisposer = _threeSecondsObservable
                .Subscribe(async time =>
                {
                    //대화시작이 않되었거나 레코딩 중이 아니라면 레코딩 종료를 하지 않음
                    if (_isConversation == false || _isRecoding == false) return;

                    //여기 지워지면 잘 될 것 같음
                    //_isRecoding = false;
                    await Singleton<MicrophoneHelper>.Instance.StopRecordingAsync();
                    OnClientStateChanging(ClientStates.StopRecoding, _recodingFileName);
                });

        }

        /// <summary>
        /// 상태 변경 이벤트
        /// </summary>
        protected virtual void OnClientStateChanging(ClientStates state, object data = null)
        {
            var args = new ClientState
            {
                States = state,
                Data = data
            };
            ClientStateChanging?.Invoke(this, args);
        }

        /// <summary>
        /// 상태 변경 이벤트
        /// </summary>
        public event EventHandler<ClientState> ClientStateChanging;

        /// <summary>
        ///     시스템 메시지 출력
        /// </summary>
        /// <param name="messageFileName"></param>
        private async void PlaySystemMessage(string messageFileName)
        {
            var filePath = new Uri($"ms-appx:///Assets/SystemVoices/{messageFileName}");
            var file = await StorageFile.GetFileFromApplicationUriAsync(filePath);
            try
            {
                RandomAccessStream = await file.OpenAsync(FileAccessMode.Read);
                OnClientStateChanging(ClientStates.PlaySystemVoice, messageFileName);
            }
            catch (FileNotFoundException)
            {
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        /// <summary>
        ///     랜덤 엑세스 스트림
        /// </summary>
        public IRandomAccessStream RandomAccessStream
        {
            get => _randomAccessStream;
            set => Set(ref _randomAccessStream, value);
        }

        /// <summary>
        ///     미디어 종료 커맨드
        /// </summary>
        public ICommand MediaEndedCommand { get; set; }
    }
}
