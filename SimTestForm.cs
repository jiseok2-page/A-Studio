using AppBase;
using AudioViewStudio.Analysis;
using AudioViewStudio.Audio.Processing;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;


namespace AudioViewStudio
{
    public partial class SimTestForm : Form
    {
        // Win32 API for keyboard state detection
        //[DllImport("user32.dll")]
        //private static extern short GetKeyState(int vKey);
        //[DllImport("user32.dll")]
        //private static extern short GetAsyncKeyState(int vKey);  // 실시간 키 상태 확인 (더 정확함)
        //private const int VK_CONTROL = 0x11;   // Ctrl 키 (일반)
        //private const int VK_LCONTROL = 0xA2;  // 왼쪽 Ctrl
        //private const int VK_RCONTROL = 0xA3;  // 오른쪽 Ctrl

        public MainForm pa = null;
        private readonly SimilarityChecksetHandler _checksetHandler = new SimilarityChecksetHandler();

        public AudioFptFileInfo _fptFile = new AudioFptFileInfo(); // 오디오 소스(wav, csv) 경로 정보

        //private CancellationTokenSource _testCts;
        private CancellationTokenSource _fingerprintMatchingCts;
        
        // 핑거프린트 실시간 매칭 관련 필드
        private IWaveIn _waveIn; // ★ WaveInEvent -> IWaveIn 변경 (WasapiLoopbackCapture 호환) ★
        private System.Windows.Forms.Timer _matchingTimer;
        private List<float> _audioBuffer = new List<float>();
        private readonly object _audioBufferLock = new object(); // _audioBuffer 동기화용
        private float[] _prevAudioTail; // ★ 오버랩 처리를 위한 이전 버퍼의 마지막 부분 ★
        private readonly object _matchesLock = new object(); // _consecutiveMatches 동기화용
        
        // ★ High Pass Filter 상태 변수 (DC/LowFreq 제거용) ★
        private float _hpfPrevIn = 0f;
        private float _hpfPrevOut = 0f;
        
        // ★★★ 2026-02-06: 마이크 캘리브레이션 (MicCalibrator 클래스 사용) ★★★
        private MicCalibrator _micCalibrator = new MicCalibrator();
        
        // ★★★ 2026-02-07: 스트리밍 전처리기 (윈도우 간 연속성 보장) ★★★
        private StreamingPreprocessor _streamingPreprocessor = null;
        
        // ★ 디버그용 오디오 저장 (캡처 품질 확인용) ★
        private WaveFileWriter _debugWriter;
        
        // ★ 마이크 녹음 관련 (사용자 요청: Line 1642 Gain 16x 참조) ★
        // private AudioFileReader _simReader; // 제거
        // private System.Windows.Forms.Timer _simTimer; // 제거
        // private bool _useFileInjection = false; // 제거

        private TimeSpan _expectTime = new TimeSpan();
        //private const int BufferWindowSeconds = 3; // 3초 윈도우
        //private const int SampleRate = 44100;
        private int _consecutiveMatches = 0;
        private int _cumulativeRecordingSeconds = 0;  // ★ 누적 녹음 시간 (초) - 타임스탬프 오프셋용 ★
        
        // ★★★ 특징 없는 구간/불확실 구간 감지 상수 ★★★
        private const int MinHashThreshold = 3;          // 이 값 미만이면 "특징 없는 구간"으로 판정
        private const double MinConcentrationLog = 0.03; // 이 값 미만이면 "불확실 구간" 로그
        private int _consecutiveLowFeatureCount = 0;     // 연속 저특징 구간 카운트
        private const int MaxConsecutiveLowFeature = 10; // 10초 연속 저특징이면 알림
        //private const int RequiredConsecutiveMatches = 3;

        // 마이크 단순 녹음(버튼 토글) 관련 필드
        private WaveInEvent _micWaveIn;
        private NAudio.Wave.WasapiLoopbackCapture _loopbackCapture; // Loopback 캡처용
        private WaveFileWriter _micWriter;
        private bool _isMicRecording = false;
        private bool _isLoopbackMode = false; // Loopback 모드 여부
        private System.Windows.Forms.Timer _micRecordingTimer; // 마이크 녹음 자동 종료 타이머
        private DateTime _micRecordingStartTime; // 녹음 시작 시간
        private TimeSpan _micRecordingDuration; // 녹음 지속 시간
        
        // 오디오 재생 관련 필드
        private WaveOutEvent _waveOut;
        private AudioFileReader _audioFileReader;
        private bool _isPlaying = false;
        private bool _isSpeakerOn = true; // 스피커 On/Off 상태 (기본값: On)
        private System.Windows.Forms.Timer _playProgressTimer; // 재생 진행률 추적 타이머
        private System.Windows.Forms.Timer _fingerprintLoadTimer; // 핑거프린트 로드 중 깜빡임 타이머
        private bool _isFingerprintLoading = false; // 핑거프린트 로드 중 여부
        private TimeSpan _totalDuration = TimeSpan.Zero; // 전체 재생 시간
        private bool _isUserDragging = false; // 사용자가 TrackBar를 드래그 중인지 여부
        private bool _isUserEditingTime = false; // 사용자가 txtkTimePicked를 편집 중인지 여부
        private bool _specifiedTimeMatchingTriggered = false; // ★ 지정 시간 매칭 트리거 여부 (중복 방지) ★

        // 영화 핑거프린트 저장 필드
        private List<FptEntry> _movieFp = null; // 영화의 핑거프린트
        private Dictionary<ulong, List<int>> _movieRvsIndex = null; // 영화의 역인덱스 (매칭 성능 향상)
        private double? _lastOffsetConcentration = null; // 최근 매칭의 오프셋 집중도
        private RealtimeFingerprintMatcher _matcher; // 실시간 매칭 엔진 -- Added 2026-01-15, refered from claude RealtimeFingerprintMatcher.cs
        
        // 이벤트
        public event EventHandler<MatchEventArgs> StableMatchFound;
        private bool _isMatching = false; // Live Pick 실시간 매칭 동작 여부
        // UI Control
        private bool UPDATE_DATA_FROM_UI = false;
        private TimeSpan? _currentOriginalBestMatchTime; // 최적 유사도 시점 (빨간색으로 표시)
        private TimeSpan? _currentClickedTime; // 마우스 클릭 시점 (파란색으로 표시)
        private IReadOnlyList<SimilarityPoint> _currentOverallSimilaritySeries; // 전체 유사도 시리즈 저장

        ChartArea elemChart = new ChartArea(); // 특징항목별 차트 영역을 그리는 객체

        public SimTestForm()
        {
            InitializeComponent();
            
            // dgvPickedFeatures 컬럼 초기화
            InitializePickedFeaturesColumns();
            
            // picSimilarityGraph 클릭 이벤트 핸들러 추가
            picSimilarityGraph.MouseClick += PicSimilarityGraph_MouseClick;
            
            // btnPlayOrHold 클릭 이벤트 핸들러 추가
            btnPlayOrHold.Click += BtnPlayOrHold_Click;
            
            // btnSpeakOnOff 클릭 이벤트 핸들러 추가
            btnSpeakOnOff.Click += BtnSpeakOnOff_Click;
            
            // btnStopPlaying 클릭 이벤트 핸들러 추가
            btnStopPlaying.Click += BtnStopPlaying_Click;
            
            // btnNoiseFilterSettings 클릭 이벤트 핸들러 추가
            if (btnNoiseFilterSettings != null)
            {
                btnNoiseFilterSettings.Click += BtnNoiseFilterSettings_Click;
            }
            
            // dgvPickedFeatures DoubleClick 이벤트 핸들러 추가
            if (dgvPickedFpts != null)
            {
                dgvPickedFpts.CellDoubleClick += DgvPickedFeatures_CellDoubleClick;
                // 마우스 오른쪽 클릭 시 행 선택
                dgvPickedFpts.CellMouseDown += DgvPickedFeatures_CellMouseDown;
                InitializePickedFeaturesContextMenu();
                dgvPickedFpts.KeyDown += DgvPickedFpts_KeyDown;
            }
            
            // 초기 버튼 아이콘 설정
            UpdatePlayHoldButtonIcon();
            UpdateSpeakerButtonIcon();
            UpdateStopButtonIcon();
            
            // 재생 진행률 타이머 초기화
            _playProgressTimer = new System.Windows.Forms.Timer();
            _playProgressTimer.Interval = 100; // 100ms마다 업데이트
            _playProgressTimer.Tick += PlayProgressTimer_Tick;
            
            // 핑거프린트 로드 깜빡임 타이머 초기화
            _fingerprintLoadTimer = new System.Windows.Forms.Timer();
            _fingerprintLoadTimer.Interval = 500; // 500ms마다 깜빡임
            _fingerprintLoadTimer.Tick += FingerprintLoadTimer_Tick;
            
            // 마이크 녹음 자동 종료 타이머 초기화
            _micRecordingTimer = new System.Windows.Forms.Timer();
            _micRecordingTimer.Interval = 100; // 100ms마다 체크
            _micRecordingTimer.Tick += MicRecordingTimer_Tick;
            
            // TrackBar 드래그 이벤트 연결
            if (trackBarPlayProgress != null)
            {
                trackBarPlayProgress.MouseDown += (s, e) => { _isUserDragging = true; };
                trackBarPlayProgress.MouseUp += (s, e) => { _isUserDragging = false; };
            }
            
            // txtkTimePicked 입력 제한 및 형식 변환 이벤트 연결
            if (txtTimePlay != null)
            {
                txtTimePlay.KeyPress += TxtTimePicked_KeyPress;
                txtTimePlay.Enter += TxtTimePicked_Enter;
                txtTimePlay.Leave += TxtTimePicked_Leave;
            }
            
            // txtSustainTime 입력 제한 및 형식 변환 이벤트 연결
            if (txtSustainTime != null)
            {
                txtSustainTime.KeyPress += TxtSustainTime_KeyPress;
                txtSustainTime.Enter += TxtSustainTime_Enter;
                txtSustainTime.Leave += TxtSustainTime_Leave;
            }
            
            // txtTimeTry 입력 제한 및 형식 변환 이벤트 연결 (HH:mm:ss.nnn 형식)
            if (txtTimeTry != null)
            {
                txtTimeTry.KeyPress += TxtTimeTry_KeyPress;
                txtTimeTry.KeyDown += TxtTimeTry_KeyDown;
                txtTimeTry.KeyUp += TxtTimeTry_KeyUp;
                txtTimeTry.Enter += TxtTimeTry_Enter;
                txtTimeTry.Leave += TxtTimeTry_Leave;
            }
            
            // btnPickSnipet 클릭 이벤트 연결
            if (btnPickSnipet != null)
            {
                btnPickSnipet.Click += BtnPickSnipet_Click;
            }
            
            // btnLivePickSnipet 클릭 이벤트 연결 (실시간 매칭 시작/중지)
            if (btnLivePickSnipet != null)
            {
                btnLivePickSnipet.Click += BtnLivePickSnipet_Click;
            }

            // btnFlagMic 클릭 시 마이크 녹음 시작/중지
            if (btnFlagMic != null)
            {
                btnFlagMic.MouseDown += BtnFlagMic_MouseDown;
            }
            
            
            // cboSnippetLength 선택 변경 이벤트 연결
            if (cboSnippetTermMs != null)
            {
                cboSnippetTermMs.SelectedIndexChanged += CboSnippetLength_SelectedIndexChanged;
            }

            RegisterOptionalParamEvents();
            RegisterPreprocessParamEvents();
            RegisterOffsetParamEvents();
        }


        public SimilarityAnalysisOptions GetOptions()
        {
            return new SimilarityAnalysisOptions
            {
                SelectedCheckElems = new List<SimilarityCheckElement>()
            };
        }

        private void btnOk_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.OK;
            Close();
        }

        public void SetFileIds(AudioFptFileInfo fptFile)
        {
            _fptFile = fptFile;
            //pickParam.fptFile = filePaths;
            // 서버 등록 영화 ID 설정
            if (lblCurrMovieId != null)
            {
                lblCurrMovieId.Text = _fptFile.svrMovieID;
            }

            // 오디오 파일명 표시
            if (!string.IsNullOrWhiteSpace(_fptFile.mvAudioFile))
            {
                lbl_MovieAudioFile.Text = _fptFile.mvAudioFile;
            }
            else
            {
                lbl_MovieAudioFile.Text = "없음";
            }
        }

        private void SimilarityTestForm_Load(object sender, EventArgs e)
        {
            // 영화 ID가 설정되지 않았으면 "미등록"으로 설정
            if (lblCurrMovieId != null && (string.IsNullOrWhiteSpace(lblCurrMovieId.Text) || lblCurrMovieId.Text == "ID"))
            {
                lblCurrMovieId.Text = "미등록";
            }
            var susTain = pa.prof.GetItem(PDN.S_PARAMS, PDN.E_SIMILTEST, PDN.I_DURATIME);
            txtSustainTime.Text = (susTain.bValid && !string.IsNullOrEmpty(susTain.sValue)) ? susTain.sValue : "00:00:05";

            var syncRec = pa.prof.GetItem(PDN.S_PARAMS, PDN.E_SIMILTEST, PDN.I_PLAYSYNCREC);
            chkRecSyncWithPlay.Checked = (syncRec.bValid && syncRec.sValue.Equals("on")) ? true : false;

            var onTime = pa.prof.GetItem(PDN.S_PARAMS, PDN.E_SIMILTEST, PDN.I_MATCHONTIME);
            chkSpecifiedTime.Checked = (onTime.bValid && onTime.sValue.Equals("on")) ? true : false;
            if(chkSpecifiedTime.Checked)
            {
                var time = pa.prof.GetItem(PDN.S_PARAMS, PDN.E_SIMILTEST, PDN.I_SPECTIME);
                txtTimeTry.Text = (time.bValid && !string.IsNullOrEmpty(time.sValue)) ? time.sValue : "00:00:00.000";
            }

            var lRec = pa.prof.GetItem(PDN.S_PARAMS, PDN.E_SIMILTEST, PDN.I_LOOPBACK);
            chkLoopbackRec.Checked = (lRec.bValid && lRec.sValue.Equals("on")) ? true : false;

            // Wave 파일 존재 여부 확인 및 btnFlagWav 색상 설정
            UpdateWaveFlag();

            // 핑거프린트 파일 존재 여부 확인 및 btnFlagFpf 색상 설정
            UpdateFingerprintFlag();

            // 마이크 장치 상태 확인 및 btnFlagMic 색상 설정
            UpdateMicFlag();

            // 영화 핑거프린트 로드
            LoadMovieFingerprint();

            // 프로필에서 스니펫 길이 설정 복원
            LoadSnippetLengthFromProfile();
            // 기존 *.fp.mpack 파일들을 dgvPickedFeatures에 등록
            LoadExistingPickedFingerprints();

            ApplyOptionalParamToUI();
            ApplyPreprocessParamToUI();
            ApplyOffsetParamToUI();
        }

        private void btnFlagFpf_Click(object sender, EventArgs e)
        {
            // 영화 핑거프린트 파일을 다시 로드
            LoadMovieFingerprint();
        }

        private void RegisterOptionalParamEvents()
        {
            chkAdaptiveDynamic.CheckedChanged += (s, e) => UpdateOptionalParamsFromUI();
            chkQBaseFiltering.CheckedChanged += (s, e) => UpdateOptionalParamsFromUI();
            numQualityThreshold.ValueChanged += (s, e) => UpdateOptionalParamsFromUI();
        }
        private void RegisterPreprocessParamEvents()
        {
            if (numHpCutoffHz != null) numHpCutoffHz.ValueChanged += (s, e) => UpdatePreprocessParamsFromUI();
            if (numBaseGateMultiplier != null) numBaseGateMultiplier.ValueChanged += (s, e) => UpdatePreprocessParamsFromUI();
            if (numAttackMs != null) numAttackMs.ValueChanged += (s, e) => UpdatePreprocessParamsFromUI();
            if (numReleaseMs != null) numReleaseMs.ValueChanged += (s, e) => UpdatePreprocessParamsFromUI();
            if (numTargetRms != null) numTargetRms.ValueChanged += (s, e) => UpdatePreprocessParamsFromUI();
            if (numClipDrive != null) numClipDrive.ValueChanged += (s, e) => UpdatePreprocessParamsFromUI();
        }

        private void RegisterOffsetParamEvents()
        {
            if (numOffsetConcntThreshold != null) numOffsetConcntThreshold.ValueChanged += (s, e) => UpdateOffsetParamsFromUI();
            if (numMaxWindowSizeMs != null) numMaxWindowSizeMs.ValueChanged += (s, e) => UpdateOffsetParamsFromUI();
            if (numMaxWindowSizeMsLowOffset != null) numMaxWindowSizeMsLowOffset.ValueChanged += (s, e) => UpdateOffsetParamsFromUI();
            if (numGateSoftnessMultiplier != null) numGateSoftnessMultiplier.ValueChanged += (s, e) => UpdateOffsetParamsFromUI();
            if (numGateSoftnessMultiplierLowOffset != null) numGateSoftnessMultiplierLowOffset.ValueChanged += (s, e) => UpdateOffsetParamsFromUI();
        }

        private void ApplyPreprocessParamToUI()
        {
            numHpCutoffHz.Value = ClampToRange((decimal)pa.workTab.pickParam.PP.hpCutoffHz, numHpCutoffHz.Minimum, numHpCutoffHz.Maximum);
            numBaseGateMultiplier.Value = ClampToRange((decimal)pa.workTab.pickParam.PP.baseGateMultiplier, numBaseGateMultiplier.Minimum, numBaseGateMultiplier.Maximum);
            numAttackMs.Value = ClampToRange((decimal)pa.workTab.pickParam.PP.attackMs, numAttackMs.Minimum, numAttackMs.Maximum);
            numReleaseMs.Value = ClampToRange((decimal)pa.workTab.pickParam.PP.releaseMs, numReleaseMs.Minimum, numReleaseMs.Maximum);
            numTargetRms.Value = ClampToRange((decimal)pa.workTab.pickParam.PP.targetRms, numTargetRms.Minimum, numTargetRms.Maximum);
            numClipDrive.Value = ClampToRange((decimal)pa.workTab.pickParam.PP.clipDrive, numClipDrive.Minimum, numClipDrive.Maximum);
        }

        private void ApplyOffsetParamToUI()
        {
            numOffsetConcntThreshold.Value = ClampToRange((decimal)pa.workTab.pickParam.offsetConcntThreshold, numOffsetConcntThreshold.Minimum, numOffsetConcntThreshold.Maximum);
            numMaxWindowSizeMs.Value = ClampToRange(pa.workTab.pickParam.maxWindowSizeMs, numMaxWindowSizeMs.Minimum, numMaxWindowSizeMs.Maximum);
            numMaxWindowSizeMsLowOffset.Value = ClampToRange(pa.workTab.pickParam.maxWindowSizeMsLowOffset, numMaxWindowSizeMsLowOffset.Minimum, numMaxWindowSizeMsLowOffset.Maximum);
            numGateSoftnessMultiplier.Value = ClampToRange((decimal)pa.workTab.pickParam.gateSoftnessMultiplier, numGateSoftnessMultiplier.Minimum, numGateSoftnessMultiplier.Maximum);
            numGateSoftnessMultiplierLowOffset.Value = ClampToRange((decimal)pa.workTab.pickParam.gateSoftnessMultiplierLowOffset, numGateSoftnessMultiplierLowOffset.Minimum, numGateSoftnessMultiplierLowOffset.Maximum);
        }
        private void ApplyOptionalParamToUI()
        {
            chkAdaptiveDynamic.Checked = pa.workTab.pickParam.adaptiveDynamic;
            chkQBaseFiltering.Checked = pa.workTab.pickParam.UseQualityBasedFiltering;
            numQualityThreshold.Value = ClampToRange((decimal)pa.workTab.pickParam.QualityThreshold, numQualityThreshold.Minimum, numQualityThreshold.Maximum);

            UPDATE_DATA_FROM_UI = true;
        }
        private void UpdateOptionalParamsFromUI()
        {
            if (!UPDATE_DATA_FROM_UI) return;
            pa.workTab.pickParam.adaptiveDynamic = chkAdaptiveDynamic.Checked;
            pa.workTab.pickParam.UseQualityBasedFiltering = chkQBaseFiltering.Checked;
            pa.workTab.pickParam.QualityThreshold = (double)numQualityThreshold.Value;
        }
        private void UpdatePreprocessParamsFromUI()
        {
            pa.workTab.pickParam.PP.hpCutoffHz = (double)numHpCutoffHz.Value;
            pa.workTab.pickParam.PP.baseGateMultiplier = (double)numBaseGateMultiplier.Value;
            pa.workTab.pickParam.PP.attackMs = (double)numAttackMs.Value;
            pa.workTab.pickParam.PP.releaseMs = (double)numReleaseMs.Value;
            pa.workTab.pickParam.PP.targetRms = (double)numTargetRms.Value;
            pa.workTab.pickParam.PP.clipDrive = (double)numClipDrive.Value;
        }

        private void UpdateOffsetParamsFromUI()
        {
            pa.workTab.pickParam.offsetConcntThreshold = (double)numOffsetConcntThreshold.Value;
            pa.workTab.pickParam.maxWindowSizeMs = (int)numMaxWindowSizeMs.Value;
            pa.workTab.pickParam.maxWindowSizeMsLowOffset = (int)numMaxWindowSizeMsLowOffset.Value;
            pa.workTab.pickParam.gateSoftnessMultiplier = (double)numGateSoftnessMultiplier.Value;
            pa.workTab.pickParam.gateSoftnessMultiplierLowOffset = (double)numGateSoftnessMultiplierLowOffset.Value;
        }

        private static decimal ClampToRange(decimal value, decimal min, decimal max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        /// <summary>
        /// 마이크 장치 상태를 확인하고 btnFlagMic 색상을 업데이트합니다.
        /// </summary>
        private void UpdateMicFlag()
        {
            if (btnFlagMic == null)
            {
                return;
            }

            try
            {
                int deviceCount = WaveIn.DeviceCount;
                if (deviceCount > 0)
                {
                    // 마이크 장치가 정상 동작 중이면 녹색
                    btnFlagMic.BackColor = Color.FromArgb(150, 100, 200, 100); // 녹색계통
                }
                else
                {
                    // 마이크 장치가 없으면 회색
                    btnFlagMic.BackColor = SystemColors.ButtonFace;
                }
            }
            catch (Exception ex)
            {
                // 마이크 장치 확인 실패 시 회색으로 설정
                System.Diagnostics.Debug.WriteLine($"UpdateMicFlag 오류: {ex.Message}");
                btnFlagMic.BackColor = SystemColors.ButtonFace;
            }
        }

        /// <summary>
        /// Wave 파일 존재 여부를 확인하고 btnFlagWav 색상을 업데이트합니다.
        /// </summary>
        private void UpdateWaveFlag()
        {
            if (btnFlagWav == null)
            {
                return;
            }

            try
            {
                // audioDir에서 wave 파일 확인
                if (string.IsNullOrWhiteSpace(_fptFile.movieFolder) || !Directory.Exists(_fptFile.movieFolder))
                {
                    // 디렉토리가 없으면 회색
                    btnFlagWav.BackColor = SystemColors.ButtonFace;
                    return;
                }

                // Wave 파일 찾기 (*.wav)
                if (!string.IsNullOrWhiteSpace(_fptFile.mvAudioFile))
                {
                    string waveFilePath = Path.Combine(_fptFile.movieFolder, _fptFile.mvAudioFile);
                    if (File.Exists(waveFilePath))
                    {
                        // Wave 파일이 있으면 녹색
                        btnFlagWav.BackColor = Color.FromArgb(150, 100, 200, 100); // 녹색계통
                    }
                    else
                    {
                        // Wave 파일이 없으면 회색
                        btnFlagWav.BackColor = SystemColors.ButtonFace;
                    }
                }
                else
                {
                    // 파일명이 없으면 회색
                    btnFlagWav.BackColor = SystemColors.ButtonFace;
                }
            }
            catch (Exception ex)
            {
                // 오류 발생 시 회색으로 설정
                System.Diagnostics.Debug.WriteLine($"UpdateWaveFlag 오류: {ex.Message}");
                btnFlagWav.BackColor = SystemColors.ButtonFace;
            }
        }

        /// <summary>
        /// 핑거프린트 파일 존재 여부를 확인하고 btnFlagFpf 색상을 업데이트합니다.
        /// </summary>
        private void UpdateFingerprintFlag()
        {
            if (btnFlagMovieFpt == null)
            {
                return;
            }

            try
            {
                // 핑거프린트 파일 찾기 (*.json)
                if (File.Exists(_fptFile.GetFeatureFilePath()))
                {
                    // 핑거프린트 파일이 있으면 녹색
                    btnFlagMovieFpt.BackColor = Color.FromArgb(150, 100, 200, 100); // 녹색계통
                }
                else
                {
                    // 핑거프린트 파일이 없으면 회색
                    btnFlagMovieFpt.BackColor = SystemColors.ButtonFace;
                }
            }
            catch (Exception ex)
            {
                // 오류 발생 시 회색으로 설정
                System.Diagnostics.Debug.WriteLine($"UpdateFingerprintFlag 오류: {ex.Message}");
                btnFlagMovieFpt.BackColor = SystemColors.ButtonFace;
            }
        }


        private void RenderSimilarityGraph(IReadOnlyList<SimilarityPoint> points, TimeSpan? clickedTime = null, TimeSpan? originalBestMatchTime = null)
        {
            if (picSimilarityGraph == null || picSimilarityGraph.Width <= 0 || picSimilarityGraph.Height <= 0)
            {
                return;
            }

            if (points == null || points.Count == 0)
            {
                // 빈 그래프 그리기
                var bitmap = new Bitmap(picSimilarityGraph.Width, picSimilarityGraph.Height);
                using (var graphics = Graphics.FromImage(bitmap))
                using (var background = new SolidBrush(Color.White))
                {
                    graphics.FillRectangle(background, 0, 0, bitmap.Width, bitmap.Height);
                }
                picSimilarityGraph.Image?.Dispose();
                picSimilarityGraph.Image = bitmap;
                return;
            }

            var bitmap2 = new Bitmap(picSimilarityGraph.Width, picSimilarityGraph.Height);

            using (var graphics = Graphics.FromImage(bitmap2))
            using (var background = new SolidBrush(Color.White))
            using (var axisPen = new Pen(Color.FromArgb(220, 220, 220), 1))
            using (var gridPen = new Pen(Color.FromArgb(240, 240, 240), 1))
            using (var linePen = new Pen(Color.FromArgb(128, 128, 128), 1))
            using (var bestMatchPen = new Pen(Color.Red, 2)) // 최적 유사도 시점 (빨간색)
            using (var clickedTimePen = new Pen(Color.Blue, 2)) // 클릭한 시점 (파란색)
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillRectangle(background, 0, 0, bitmap2.Width, bitmap2.Height);

                // 시간 범위 계산
                double minSeconds = points.Min(p => p.Timestamp.TotalSeconds);
                double maxSeconds = points.Max(p => p.Timestamp.TotalSeconds);
                double timeRange = maxSeconds - minSeconds;

                if (timeRange <= 0)
                {
                    timeRange = 1;
                }

                // Y축 격자선 그리기 (유사도 0.0, 0.25, 0.5, 0.75, 1.0)
                for (int i = 0; i <= 4; i++)
                {
                    float y = 10f + (float)(i / 4.0) * (bitmap2.Height - 20);
                    graphics.DrawLine(gridPen, 10f, y, bitmap2.Width - 10f, y);
                }

                // 그래프 포인트 계산
                var drawingPoints = new List<PointF>(points.Count);

                foreach (var point in points)
                {
                    // X축: 전체 시간에 대한 비율
                    double relativeX = (point.Timestamp.TotalSeconds - minSeconds) / timeRange;
                    float x = (float)(relativeX * (bitmap2.Width - 20)) + 10f;

                    // Y축: 유사도 0.0(하단) ~ 1.0(상단)
                    float y = (float)((1 - point.Score) * (bitmap2.Height - 20)) + 10f;

                    // X축 범위 내의 점만 추가
                    if (x >= 10f && x <= bitmap2.Width - 10f)
                    {
                        drawingPoints.Add(new PointF(x, y));
                    }
                }

                // Line graph로 그리기 (점이 2개 이상일 때만)
                if (drawingPoints.Count > 1)
                {
                    // X 좌표 순서로 정렬
                    drawingPoints = drawingPoints.OrderBy(p => p.X).ToList();
                    graphics.DrawLines(linePen, drawingPoints.ToArray());
                }

                // 최적 유사도 시점에 수직선 그리기 (빨간색)
                if (originalBestMatchTime.HasValue && originalBestMatchTime.Value.TotalSeconds > 0)
                {
                    double relativeBestMatchX = (originalBestMatchTime.Value.TotalSeconds - minSeconds) / timeRange;
                    float bestMatchX = (float)(relativeBestMatchX * (bitmap2.Width - 20)) + 10f;

                    // X축 범위 내에 있을 때만 그리기
                    if (bestMatchX >= 10f && bestMatchX <= bitmap2.Width - 10f)
                    {
                        graphics.DrawLine(bestMatchPen, bestMatchX, 10f, bestMatchX, bitmap2.Height - 10f);
                    }
                }

                // 클릭한 시점에 수직선 그리기 (파란색)
                if (clickedTime.HasValue && clickedTime.Value.TotalSeconds > 0)
                {
                    double relativeClickedX = (clickedTime.Value.TotalSeconds - minSeconds) / timeRange;
                    float clickedX = (float)(relativeClickedX * (bitmap2.Width - 20)) + 10f;

                    // X축 범위 내에 있을 때만 그리기
                    if (clickedX >= 10f && clickedX <= bitmap2.Width - 10f)
                    {
                        graphics.DrawLine(clickedTimePen, clickedX, 10f, clickedX, bitmap2.Height - 10f);
                    }
                }
            }

            picSimilarityGraph.Image?.Dispose();
            picSimilarityGraph.Image = bitmap2;
        }

        /// <summary>
        /// picSimilarityGraph 마우스 클릭 이벤트 핸들러
        /// </summary>
        private void PicSimilarityGraph_MouseClick(object sender, MouseEventArgs e)
        {
            // 분석이 완료되지 않았거나 데이터가 없으면 무시
            if (_currentOverallSimilaritySeries == null ||
                _currentOverallSimilaritySeries.Count == 0 ||
                picSimilarityGraph == null)
            {
                return;
            }

            // 클릭한 X 좌표를 시간으로 변환
            int clickX = e.X;
            int graphWidth = picSimilarityGraph.Width;
            int padding = 10; // 좌우 패딩

            // 클릭 위치가 그래프 영역 내에 있는지 확인
            if (clickX < padding || clickX > graphWidth - padding)
            {
                return;
            }

            // 전체 시간 범위 계산
            double minSeconds = _currentOverallSimilaritySeries.Min(p => p.Timestamp.TotalSeconds);
            double maxSeconds = _currentOverallSimilaritySeries.Max(p => p.Timestamp.TotalSeconds);
            double timeRange = maxSeconds - minSeconds;

            if (timeRange <= 0)
            {
                return;
            }

            // X 좌표를 시간으로 변환
            double relativeX = (double)(clickX - padding) / (graphWidth - 2 * padding);
            double clickedSeconds = minSeconds + relativeX * timeRange;
            TimeSpan clickedTime = TimeSpan.FromSeconds(clickedSeconds);

            // 현재 선택된 시간 업데이트
            _currentClickedTime = clickedTime; // 클릭한 시점 저장 (파란색으로 표시)

            // 그래프 재그리기 (클릭한 시점은 파란색, 최적 유사도 시점은 빨간색)
            RenderSimilarityGraph(_currentOverallSimilaritySeries, clickedTime: clickedTime, originalBestMatchTime: _currentOriginalBestMatchTime);
        }

        /// <summary>
        /// 로딩 시간을 mm:ss.ms 형식으로 포맷팅합니다.
        /// </summary>
        /// <param name="timeSpan">로딩 시간</param>
        /// <returns>mm:ss.ms 형식의 문자열 (예: 01:23.456)</returns>
        private string FormatLoadingTime(TimeSpan timeSpan)
        {
            return SFPci.FormatLoadingTime(timeSpan);
        }

        private string FormatTimeSpan(TimeSpan timeSpan)
        {
            return SFPci.FormatTimeSpan(timeSpan);
        }


        
        /// <summary>
        /// btnPlayOrHold 클릭 이벤트 핸들러 - 음원 재생/일시정지
        /// </summary>
        private void BtnPlayOrHold_Click(object sender, EventArgs e)
        {
            if (_waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing)
            {
                // 현재 재생 중이면 일시정지
                HoldAudio();
            }
            else if (_waveOut != null && _waveOut.PlaybackState == PlaybackState.Paused)
            {
                // 일시정지 상태면 재개
                ResumeAudio();
            }
            else
            {
                // 재생 중이 아니면 재생 시작
                PlayAudio();
            }
        }

        /// <summary>
        /// 음원 재생 시작
        /// </summary>
        private void PlayAudio()
        {
            try
            {
                // lbl_MovieAudioFile에서 파일명 가져오기
                string fileName = lbl_MovieAudioFile.Text?.Trim();
                if (string.IsNullOrWhiteSpace(fileName) || fileName == "없음")
                {
                    MessageBox.Show("재생할 음원 파일이 없습니다.", "알림", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // 전체 파일 경로 구성
                string audioFilePath = null;
                if (!string.IsNullOrWhiteSpace(_fptFile.movieFolder) && Directory.Exists(_fptFile.movieFolder))
                {
                    audioFilePath = Path.Combine(_fptFile.movieFolder, fileName);
                }

                if (string.IsNullOrWhiteSpace(audioFilePath) || !File.Exists(audioFilePath))
                {
                    MessageBox.Show($"음원 파일을 찾을 수 없습니다.\n{fileName}", "파일 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // 기존 재생 중인 경우 정리
                if (_waveOut != null)
                {
                    _waveOut.Stop();
                    _waveOut.Dispose();
                    _waveOut = null;
                }
                if (_audioFileReader != null)
                {
                    _audioFileReader.Dispose();
                    _audioFileReader = null;
                }

                // 새로 재생 시작
                _audioFileReader = new AudioFileReader(audioFilePath);
                _totalDuration = _audioFileReader.TotalTime;
                _waveOut = new WaveOutEvent();
                _waveOut.Init(_audioFileReader);
                _waveOut.PlaybackStopped += (s, args) =>
                {
                    // 재생 완료 시 정리
                    if (_waveOut != null)
                    {
                        _waveOut.Dispose();
                        _waveOut = null;
                    }
                    if (_audioFileReader != null)
                    {
                        _audioFileReader.Dispose();
                        _audioFileReader = null;
                    }
                    _isPlaying = false;
                    _playProgressTimer?.Stop();
                    UpdatePlayHoldButtonIcon();
                    UpdatePlayProgress(0);
                    UpdatePlayTimeDisplay(TimeSpan.Zero);
                };

                _waveOut.Play();
                _isPlaying = true;
                _specifiedTimeMatchingTriggered = false; // ★ 지정 시간 매칭 트리거 플래그 초기화 ★
                UpdatePlayHoldButtonIcon();
                
                // 진행률 타이머 시작
                _playProgressTimer?.Start();
                UpdatePlayProgress(0);
                UpdatePlayTimeDisplay(TimeSpan.Zero);

                // ★★★ chkRecSyncWithPlay 체크 시 녹음도 동시 시작 ★★★
                if (chkRecSyncWithPlay != null && chkRecSyncWithPlay.Checked)
                {
                    StartMicOrLoopbackRecording();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"음원 재생 중 오류가 발생했습니다.\n{ex.Message}", "재생 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _isPlaying = false;
                UpdatePlayHoldButtonIcon();
            }
        }

        /// <summary>
        /// 음원 일시정지
        /// </summary>
        private void HoldAudio()
        {
            try
            {
                if (_waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing)
                {
                    _waveOut.Pause();
                    _isPlaying = false;
                    _playProgressTimer?.Stop();
                    UpdatePlayHoldButtonIcon();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"음원 일시정지 중 오류가 발생했습니다.\n{ex.Message}", "일시정지 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 음원 재개 (일시정지 후 다시 재생)
        /// </summary>
        private void ResumeAudio()
        {
            try
            {
                if (_waveOut != null && _waveOut.PlaybackState == PlaybackState.Paused)
                {
                    // NAudio에서는 Pause() 후 Play()를 호출하면 일시정지된 위치에서 계속 재생됨
                    _waveOut.Play();
                    _isPlaying = true;
                    _playProgressTimer?.Start();
                    UpdatePlayHoldButtonIcon();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"음원 재개 중 오류가 발생했습니다.\n{ex.Message}", "재개 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 재생/일시정지 버튼 아이콘 업데이트
        /// </summary>
        private void UpdatePlayHoldButtonIcon()
        {
            if (btnPlayOrHold == null)
            {
                return;
            }

            if (_isPlaying)
            {
                // 재생 중이면 일시정지 아이콘 표시
                btnPlayOrHold.Image = BitmapIcons.CreateHoldIcon();
            }
            else
            {
                // 재생 중이 아니면 재생 아이콘 표시
                btnPlayOrHold.Image = BitmapIcons.CreatePlayIcon();
            }
        }

        /// <summary>
        /// btnSpeakOnOff 클릭 이벤트 핸들러 - 스피커 On/Off
        /// </summary>
        private void BtnSpeakOnOff_Click(object sender, EventArgs e)
        {
            _isSpeakerOn = !_isSpeakerOn;
            
            // 현재 재생 중인 오디오의 볼륨 조절
            if (_audioFileReader != null)
            {
                _audioFileReader.Volume = _isSpeakerOn ? 1.0f : 0.0f;
            }
            
            UpdateSpeakerButtonIcon();
        }

        /// <summary>
        /// 스피커 On/Off 버튼 아이콘 업데이트
        /// </summary>
        private void UpdateSpeakerButtonIcon()
        {
            if (btnSpeakOnOff == null)
            {
                return;
            }

            if (_isSpeakerOn)
            {
                // 스피커 On이면 On 아이콘 표시
                btnSpeakOnOff.Image = BitmapIcons.CreateSpeakerOnIcon();
            }
            else
            {
                // 스피커 Off이면 Off 아이콘 표시
                btnSpeakOnOff.Image = BitmapIcons.CreateSpeakerOffIcon();
            }
        }

        /// <summary>
        /// btnStopPlaying 클릭 이벤트 핸들러 - 재생 중지
        /// </summary>
        private void BtnStopPlaying_Click(object sender, EventArgs e)
        {
            StopAudio();
        }

        /// <summary>
        /// 음원 재생 중지
        /// </summary>
        private void StopAudio()
        {
            try
            {
                _playProgressTimer?.Stop();
                
                if (_waveOut != null)
                {
                    _waveOut.Stop();
                    _waveOut.Dispose();
                    _waveOut = null;
                }
                if (_audioFileReader != null)
                {
                    _audioFileReader.Dispose();
                    _audioFileReader = null;
                }
                
                _isPlaying = false;
                _totalDuration = TimeSpan.Zero;
                UpdatePlayHoldButtonIcon();
                UpdatePlayProgress(0);
                UpdatePlayTimeDisplay(TimeSpan.Zero);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"음원 중지 중 오류가 발생했습니다.\n{ex.Message}", "중지 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 중지 버튼 아이콘 업데이트
        /// </summary>
        private void UpdateStopButtonIcon()
        {
            if (btnStopPlaying == null)
            {
                return;
            }

            // 중지 버튼은 항상 Stop 아이콘 표시
            btnStopPlaying.Image = BitmapIcons.CreateStopIcon();
        }

        /// <summary>
        /// 재생 진행률 타이머 Tick 이벤트 핸들러
        /// </summary>
        private void PlayProgressTimer_Tick(object sender, EventArgs e)
        {
            if (_audioFileReader != null && _totalDuration.TotalSeconds > 0)
            {
                double currentPosition = _audioFileReader.CurrentTime.TotalSeconds;
                double progress = currentPosition / _totalDuration.TotalSeconds;
                UpdatePlayProgress(progress);
                
                // 현재 재생 시간을 txtkTimePicked에 표시 (사용자가 편집 중이 아닐 때만)
                if (!_isUserEditingTime)
                {
                    UpdatePlayTimeDisplay(_audioFileReader.CurrentTime);
                }
                
                // ★★★ chkSpecifiedTime 체크 시 지정된 시간에 도달하면 실시간 매칭 시작 ★★★
                if (chkSpecifiedTime != null && chkSpecifiedTime.Checked && !_isMatching && !_specifiedTimeMatchingTriggered)
                {
                    TimeSpan? specifiedTime = ParseTimeFromTextBox(txtTimeTry);
                    if (specifiedTime.HasValue)
                    {
                        TimeSpan currentTime = _audioFileReader.CurrentTime;
                        // 지정된 시간에 도달했는지 확인 (100ms 오차 허용)
                        if (currentTime >= specifiedTime.Value && currentTime < specifiedTime.Value.Add(TimeSpan.FromMilliseconds(500)))
                        {
                            _specifiedTimeMatchingTriggered = true; // 중복 트리거 방지
                            System.Diagnostics.Debug.WriteLine($"[SimTest] 지정 시간 도달: {specifiedTime.Value} - 실시간 매칭 자동 시작");
                            BtnLivePickSnipet_Click(this, EventArgs.Empty);
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// 텍스트박스에서 시간 파싱 (HH:mm:ss 또는 HH:mm:ss.fff 형식)
        /// </summary>
        private TimeSpan? ParseTimeFromTextBox(TextBox textBox)
        {
            if (textBox == null || string.IsNullOrWhiteSpace(textBox.Text))
            {
                return null;
            }
            
            string timeText = textBox.Text.Trim();
            
            // HH:mm:ss.fff 형식 시도
            if (TimeSpan.TryParse(timeText, out TimeSpan result))
            {
                return result;
            }
            
            // HH:mm:ss 형식 시도
            string[] parts = timeText.Split(':');
            if (parts.Length == 3)
            {
                if (int.TryParse(parts[0], out int hours) &&
                    int.TryParse(parts[1], out int minutes) &&
                    double.TryParse(parts[2], out double seconds))
                {
                    return new TimeSpan(0, hours, minutes, (int)seconds, (int)((seconds - (int)seconds) * 1000));
                }
            }
            
            return null;
        }

        /// <summary>
        /// 재생 시간 표시 업데이트
        /// </summary>
        private void UpdatePlayTimeDisplay(TimeSpan currentTime)
        {
            if (txtTimePlay == null)
            {
                return;
            }
            
            // 시간:분:초.ms 형식으로 표시
            int hours = (int)currentTime.TotalHours;
            int minutes = currentTime.Minutes;
            int seconds = currentTime.Seconds;
            int milliseconds = currentTime.Milliseconds;
            
            txtTimePlay.Text = $"{hours:D2}:{minutes:D2}:{seconds:D2}.{milliseconds:D3}";
        }

        /// <summary>
        /// txtkTimePicked KeyPress 이벤트 핸들러 - 숫자만 입력 허용
        /// </summary>
        private void TxtTimePicked_KeyPress(object sender, KeyPressEventArgs e)
        {
            // 숫자(0-9), 백스페이스, Delete, Tab, Enter만 허용
            if (!char.IsDigit(e.KeyChar) && e.KeyChar != (char)Keys.Back && 
                e.KeyChar != (char)Keys.Delete && e.KeyChar != (char)Keys.Tab && 
                e.KeyChar != (char)Keys.Enter)
            {
                e.Handled = true;
            }
        }

        /// <summary>
        /// txtkTimePicked Enter 이벤트 핸들러 - 편집 시작
        /// </summary>
        private void TxtTimePicked_Enter(object sender, EventArgs e)
        {
            if (txtTimePlay == null)
            {
                return;
            }

            // 사용자가 편집을 시작함
            _isUserEditingTime = true;
        }

        /// <summary>
        /// txtkTimePicked Leave 이벤트 핸들러 - 입력 완료 시 시간 형식으로 변환
        /// </summary>
        private void TxtTimePicked_Leave(object sender, EventArgs e)
        {
            if (txtTimePlay == null)
            {
                return;
            }

            // 입력된 숫자를 밀리초로 해석
            string inputText = txtTimePlay.Text.Trim();
            
            if (string.IsNullOrEmpty(inputText))
            {
                txtTimePlay.Text = "00:00:00.000";
                _isUserEditingTime = false;
                return;
            }

            // 숫자만 추출
            string numbersOnly = new string(inputText.Where(char.IsDigit).ToArray());
            
            if (string.IsNullOrEmpty(numbersOnly))
            {
                txtTimePlay.Text = "00:00:00.000";
                _isUserEditingTime = false;
                return;
            }

            // 밀리초로 변환
            if (long.TryParse(numbersOnly, out long milliseconds))
            {
                TimeSpan timeSpan = TimeSpan.FromMilliseconds(milliseconds);
                
                // 시간:분:초.ms 형식으로 변환
                int hours = (int)timeSpan.TotalHours;
                int minutes = timeSpan.Minutes;
                int seconds = timeSpan.Seconds;
                int ms = timeSpan.Milliseconds;
                
                txtTimePlay.Text = $"{hours:D2}:{minutes:D2}:{seconds:D2}.{ms:D3}";
            }
            else
            {
                txtTimePlay.Text = "00:00:00.000";
            }

            _isUserEditingTime = false;
        }

        /// <summary>
        /// txtSustainTime KeyPress 이벤트 핸들러 - HH:mm:ss 형식 유지하면서 숫자만 교체
        /// </summary>
        private void TxtSustainTime_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (txtSustainTime == null)
            {
                e.Handled = true;
                return;
            }

            // 기본적으로 모든 입력을 처리된 것으로 표시 (직접 제어)
            e.Handled = true;

            // Tab, Enter는 기본 동작 허용
            if (e.KeyChar == (char)Keys.Tab || e.KeyChar == (char)Keys.Enter)
            {
                e.Handled = false;
                return;
            }

            // 형식이 올바른지 확인하고, 아니면 초기화
            if (txtSustainTime.Text.Length != 8 || 
                txtSustainTime.Text[2] != ':' || 
                txtSustainTime.Text[5] != ':')
            {
                txtSustainTime.Text = "00:00:00";
                txtSustainTime.SelectionStart = 0;
            }

            int caretPos = txtSustainTime.SelectionStart;

            // ':' 입력 시 다음 숫자 위치로 이동
            if (e.KeyChar == ':')
            {
                // 다음 ':' 위치 이후로 카렛 이동
                if (caretPos < 3)
                    txtSustainTime.SelectionStart = 3;
                else if (caretPos < 6)
                    txtSustainTime.SelectionStart = 6;
                return;
            }

            // 백스페이스 처리 - 이전 숫자 위치로 이동하고 0으로 교체
            if (e.KeyChar == (char)Keys.Back)
            {
                if (caretPos > 0)
                {
                    int newPos = caretPos - 1;
                    // ':' 위치면 한 칸 더 이동
                    if (newPos == 2 || newPos == 5)
                        newPos--;
                    if (newPos >= 0)
                    {
                        char[] chars = txtSustainTime.Text.ToCharArray();
                        chars[newPos] = '0';
                        txtSustainTime.Text = new string(chars);
                        txtSustainTime.SelectionStart = newPos;
                    }
                }
                return;
            }

            // 숫자 입력 처리
            if (char.IsDigit(e.KeyChar))
            {
                // 카렛이 끝에 있으면 아무것도 하지 않음
                if (caretPos >= 8)
                    return;

                // ':' 위치면 다음 위치로 이동
                if (caretPos == 2 || caretPos == 5)
                    caretPos++;

                if (caretPos < 8)
                {
                    char[] chars = txtSustainTime.Text.ToCharArray();
                    chars[caretPos] = e.KeyChar;
                    txtSustainTime.Text = new string(chars);
                    
                    // 다음 위치로 카렛 이동 (':' 건너뛰기)
                    int nextPos = caretPos + 1;
                    if (nextPos == 2 || nextPos == 5)
                        nextPos++;
                    txtSustainTime.SelectionStart = Math.Min(nextPos, 8);
                }
                return;
            }
        }

        /// <summary>
        /// txtSustainTime Enter 이벤트 핸들러 - 편집 시작
        /// </summary>
        private void TxtSustainTime_Enter(object sender, EventArgs e)
        {
            if (txtSustainTime == null)
            {
                return;
            }

            // 형식이 올바른지 확인하고, 아니면 초기화
            if (string.IsNullOrEmpty(txtSustainTime.Text) ||
                txtSustainTime.Text.Length != 8 || 
                txtSustainTime.Text[2] != ':' || 
                txtSustainTime.Text[5] != ':')
            {
                txtSustainTime.Text = "00:00:00";
            }

            // 편집 시작 시 카렛을 맨 앞에 위치
            txtSustainTime.SelectionStart = 0;
            txtSustainTime.SelectionLength = 0;
        }

        /// <summary>
        /// txtSustainTime Leave 이벤트 핸들러 - 입력 완료 시 HH:mm:ss 형식으로 변환
        /// </summary>
        private void TxtSustainTime_Leave(object sender, EventArgs e)
        {
            if (txtSustainTime == null)
            {
                return;
            }

            string inputText = txtSustainTime.Text.Trim();
            
            if (string.IsNullOrEmpty(inputText))
            {
                txtSustainTime.Text = "00:00:00";
                return;
            }

            // 이미 HH:mm:ss 형식인지 확인
            if (TimeSpan.TryParse(inputText, out TimeSpan parsedTime))
            {
                // HH:mm:ss 형식으로 변환
                int hours = (int)parsedTime.TotalHours;
                int minutes = parsedTime.Minutes;
                int seconds = parsedTime.Seconds;
                txtSustainTime.Text = $"{hours:D2}:{minutes:D2}:{seconds:D2}";
                return;
            }

            // 숫자만 추출
            string numbersOnly = new string(inputText.Where(char.IsDigit).ToArray());
            
            if (string.IsNullOrEmpty(numbersOnly))
            {
                txtSustainTime.Text = "00:00:00";
                return;
            }

            // 숫자를 초로 해석
            if (long.TryParse(numbersOnly, out long totalSeconds))
            {
                TimeSpan timeSpan = TimeSpan.FromSeconds(totalSeconds);
                
                // HH:mm:ss 형식으로 변환
                int hours = (int)timeSpan.TotalHours;
                int minutes = timeSpan.Minutes;
                int seconds = timeSpan.Seconds;
                
                txtSustainTime.Text = $"{hours:D2}:{minutes:D2}:{seconds:D2}";
            }
            else
            {
                txtSustainTime.Text = "00:00:00";
            }
            pa.prof.WriteItem(PDN.S_PARAMS, PDN.E_SIMILTEST, PDN.I_DURATIME, txtSustainTime.Text);
            pa.prof.Write_DataToFile();
        }

        private void chkRecSyncWithPlay_Click(object sender, EventArgs e)
        {
            pa.prof.WriteItem(PDN.S_PARAMS, PDN.E_SIMILTEST, PDN.I_PLAYSYNCREC, (chkRecSyncWithPlay.Checked ? "on" : "off"));
            pa.prof.Write_DataToFile();
        }
        private void chkSpecifiedTime_Click(object sender, EventArgs e)
        {
            pa.prof.WriteItem(PDN.S_PARAMS, PDN.E_SIMILTEST, PDN.I_MATCHONTIME, (chkSpecifiedTime.Checked ? "on" : "off"));
            pa.prof.Write_DataToFile();
        }
        private void chkLoopbackRec_Click(object sender, EventArgs e)
        {
            pa.prof.WriteItem(PDN.S_PARAMS, PDN.E_SIMILTEST, PDN.I_LOOPBACK, (chkLoopbackRec.Checked ? "on" : "off"));
            pa.prof.Write_DataToFile();
        }

        /// <summary>
        /// txtTimeTry KeyPress 이벤트 핸들러 - HH:mm:ss.nnn 형식 유지하면서 숫자만 교체
        /// </summary>
        private void TxtTimeTry_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (txtTimeTry == null)
            {
                e.Handled = true;
                return;
            }

            // 기본적으로 모든 입력을 처리된 것으로 표시 (직접 제어)
            e.Handled = true;

            // Tab, Enter는 기본 동작 허용
            if (e.KeyChar == (char)Keys.Tab || e.KeyChar == (char)Keys.Enter)
            {
                e.Handled = false;
                return;
            }

            // 형식이 올바른지 확인하고, 아니면 초기화 (HH:mm:ss.nnn = 12자)
            if (txtTimeTry.Text.Length != 12 || 
                txtTimeTry.Text[2] != ':' || 
                txtTimeTry.Text[5] != ':' ||
                txtTimeTry.Text[8] != '.')
            {
                txtTimeTry.Text = "00:00:00.000";
                txtTimeTry.SelectionStart = 0;
            }

            int caretPos = txtTimeTry.SelectionStart;

            // ':' 또는 '.' 입력 시 다음 숫자 위치로 이동
            if (e.KeyChar == ':' || e.KeyChar == '.')
            {
                // 다음 구분자 위치 이후로 카렛 이동
                if (caretPos < 3)
                    txtTimeTry.SelectionStart = 3;
                else if (caretPos < 6)
                    txtTimeTry.SelectionStart = 6;
                else if (caretPos < 9)
                    txtTimeTry.SelectionStart = 9;
                return;
            }

            // 백스페이스 처리 - 이전 숫자 위치로 이동하고 0으로 교체
            if (e.KeyChar == (char)Keys.Back)
            {
                if (caretPos > 0)
                {
                    int newPos = caretPos - 1;
                    // ':' 또는 '.' 위치면 한 칸 더 이동
                    if (newPos == 2 || newPos == 5 || newPos == 8)
                        newPos--;
                    if (newPos >= 0)
                    {
                        char[] chars = txtTimeTry.Text.ToCharArray();
                        chars[newPos] = '0';
                        txtTimeTry.Text = new string(chars);
                        txtTimeTry.SelectionStart = newPos;
                    }
                }
                return;
            }

            // 숫자 입력 처리
            if (char.IsDigit(e.KeyChar))
            {
                // 카렛이 끝에 있으면 아무것도 하지 않음
                if (caretPos >= 12)
                    return;

                // ':' 또는 '.' 위치면 다음 위치로 이동
                if (caretPos == 2 || caretPos == 5 || caretPos == 8)
                    caretPos++;

                if (caretPos < 12)
                {
                    char[] chars = txtTimeTry.Text.ToCharArray();
                    chars[caretPos] = e.KeyChar;
                    txtTimeTry.Text = new string(chars);
                    
                    // 다음 위치로 카렛 이동 (':' 또는 '.' 건너뛰기)
                    int nextPos = caretPos + 1;
                    if (nextPos == 2 || nextPos == 5 || nextPos == 8)
                        nextPos++;
                    txtTimeTry.SelectionStart = Math.Min(nextPos, 12);
                }
                return;
            }
        }
        /// <summary>
        /// txtTimeTry KeyDown 이벤트 핸들러 - Delete 키 동작 방지
        /// </summary>
        private void TxtTimeTry_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete)
            {
                int caretPos = txtTimeTry.SelectionStart;
                // Delete 키 처리 - 현재 위치의 숫자를 0으로 교체
                if (caretPos < 12)
                {
                    int targetPos = caretPos;
                    // ':' 또는 '.' 위치면 다음 위치로 이동
                    if (targetPos == 2 || targetPos == 5 || targetPos == 8)
                        targetPos++;
                    if (targetPos < 12)
                    {
                        char[] chars = txtTimeTry.Text.ToCharArray();
                        chars[targetPos] = '0';
                        txtTimeTry.Text = new string(chars);
                        txtTimeTry.SelectionStart = targetPos;
                    }
                }

                e.SuppressKeyPress = true; // Delete 키 동작 방지
            }
        }
        private void TxtTimeTry_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete)
            {
                //string tmp_time = txtTimeTry.Text;
                //if(!isTimeMsFormat(tmp_time))
                //{
                //    txtTimeTry.Text = "00:00:00.000";
                //}
            }
        }

        private bool isTimeMsFormat(string timeText)
        {
            if (timeText.Length != 12 ||
                timeText[2] != ':' ||
                timeText[5] != ':' ||
                timeText[8] != '.')
            {
                return false;
            }
            return true;
        }
        /// <summary>
        /// txtTimeTry Enter 이벤트 핸들러 - 편집 시작
        /// </summary>
        private void TxtTimeTry_Enter(object sender, EventArgs e)
        {
            if (txtTimeTry == null)
            {
                return;
            }

            // 형식이 올바른지 확인하고, 아니면 초기화
            if (string.IsNullOrEmpty(txtTimeTry.Text) ||
                txtTimeTry.Text.Length != 12 || 
                txtTimeTry.Text[2] != ':' || 
                txtTimeTry.Text[5] != ':' ||
                txtTimeTry.Text[8] != '.')
            {
                txtTimeTry.Text = "00:00:00.000";
            }

            // 편집 시작 시 카렛을 맨 앞에 위치
            txtTimeTry.SelectionStart = 0;
            txtTimeTry.SelectionLength = 0;
        }

        /// <summary>
        /// txtTimeTry Leave 이벤트 핸들러 - 입력 완료 시 HH:mm:ss.nnn 형식 유지
        /// </summary>
        private void TxtTimeTry_Leave(object sender, EventArgs e)
        {
            if (txtTimeTry == null)
            {
                return;
            }

            //string inputText = txtTimeTry.Text.Trim();
            
            //if (string.IsNullOrEmpty(inputText))
            //{
            //    txtTimeTry.Text = "00:00:00.000";
            //    return;
            //}

            //// 이미 HH:mm:ss.nnn 형식인지 확인
            //if (inputText.Length == 12 && 
            //    inputText[2] == ':' && 
            //    inputText[5] == ':' && 
            //    inputText[8] == '.')
            //{
            //    return; // 형식이 올바르면 그대로 유지
            //}

            //// 형식이 올바르지 않으면 초기화
            //txtTimeTry.Text = "00:00:00.000";
            
            if (chkSpecifiedTime.Checked)
            {
                pa.prof.WriteItem(PDN.S_PARAMS, PDN.E_SIMILTEST, PDN.I_SPECTIME, txtTimeTry.Text);
                pa.prof.Write_DataToFile();
            }
        }
        /// <summary>
        /// 재생 진행률 업데이트
        /// </summary>
        private void UpdatePlayProgress(double progress)
        {
            if (trackBarPlayProgress == null)
            {
                return;
            }

            // 0.0 ~ 1.0 범위를 0 ~ 1000 범위로 변환
            int value = (int)(progress * 1000);
            value = Math.Max(0, Math.Min(1000, value)); // 0~1000 범위로 제한

            if (!_isUserDragging)
            {
                trackBarPlayProgress.Value = value;
            }

            // Panel을 다시 그려서 세로선 업데이트
            pnlPlayProgress?.Invalidate();
        }

        /// <summary>
        /// TrackBar ValueChanged 이벤트 핸들러 - 사용자가 위치를 변경할 때
        /// </summary>
        private void TrackBarPlayProgress_ValueChanged(object sender, EventArgs e)
        {
            if (_isUserDragging && _audioFileReader != null && _totalDuration.TotalSeconds > 0)
            {
                // TrackBar 값(0~1000)을 시간으로 변환
                double progress = trackBarPlayProgress.Value / 1000.0;
                TimeSpan newPosition = TimeSpan.FromSeconds(_totalDuration.TotalSeconds * progress);
                
                // 오디오 재생 위치 변경
                _audioFileReader.CurrentTime = newPosition;
                
                // Panel을 다시 그려서 세로선 업데이트
                pnlPlayProgress?.Invalidate();
            }
        }

        /// <summary>
        /// Panel Paint 이벤트 핸들러 - 현재 재생 위치에 세로선 그리기
        /// </summary>
        private void PnlPlayProgress_Paint(object sender, PaintEventArgs e)
        {
            if (trackBarPlayProgress == null || pnlPlayProgress == null)
            {
                return;
            }

            // TrackBar의 현재 위치 계산
            int trackBarWidth = trackBarPlayProgress.Width;
            int trackBarLeft = trackBarPlayProgress.Left;
            int maxValue = trackBarPlayProgress.Maximum;
            int currentValue = trackBarPlayProgress.Value;
            
            // 현재 재생 위치에 해당하는 X 좌표 계산
            float progressRatio = maxValue > 0 ? (float)currentValue / maxValue : 0f;
            int lineX = trackBarLeft + (int)(trackBarWidth * progressRatio);
            
            // 세로선 그리기
            using (Pen pen = new Pen(Color.Red, 2))
            {
                e.Graphics.DrawLine(pen, lineX, 0, lineX, pnlPlayProgress.Height);
            }
        }

        /// <summary>
        /// 키보드 단축키 처리
        /// Ctrl+Shift+D: Live 핑거프린트 진단 실행
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Ctrl+Shift+D: Live 핑거프린트 진단
            if (keyData == (Keys.Control | Keys.Shift | Keys.D))
            {
                _ = RunLiveFingerprintDiagnostic();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>
        /// 폼 종료 시 리소스 정리
        /// </summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 핑거프린트 매칭 중단
            StopMatching();
            
            // 오디오 재생 정리
            StopAudio();

            // 마이크 녹음 정리
            StopMicRecording();
            
            // 타이머 정리
            if (_playProgressTimer != null)
            {
                _playProgressTimer.Stop();
                _playProgressTimer.Dispose();
                _playProgressTimer = null;
            }
            
            if (_fingerprintLoadTimer != null)
            {
                _fingerprintLoadTimer.Stop();
                _fingerprintLoadTimer.Dispose();
                _fingerprintLoadTimer = null;
            }
            
            if (_micRecordingTimer != null)
            {
                _micRecordingTimer.Stop();
                _micRecordingTimer.Dispose();
                _micRecordingTimer = null;
            }

            // 설정이 변경되었으면 프로필에 저장
             if (pa.workTab.pickParam.IsChanged(pa.workTab.pickParOrg))
            {
                pa.workTab.pickParOrg.Update(pa.workTab.pickParam);

                pa.prof.WriteString(PDN.S_FINGERPRINT, PDN.E_FPT_PARAMS, pa.workTab.pickParam.ParamsToString());
                pa.prof.Write_DataToFile();
            }

            // event args 전달
            base.OnFormClosing(e);
        }

        /// <summary>
        /// dgvPickedFeatures 컬럼 초기화
        /// </summary>
        private void InitializePickedFeaturesColumns()
        {
            if (dgvPickedFpts == null)
            {
                return;
            }

            // 기존 컬럼 모두 제거
            dgvPickedFpts.Columns.Clear();

            // # 컬럼 추가
            var colNumber = new DataGridViewTextBoxColumn
            {
                Name = "colNumber",
                HeaderText = "#",
                ReadOnly = true,
                Width = 40
            };
            dgvPickedFpts.Columns.Add(colNumber);

            // Picked 컬럼 추가
            var colPicked = new DataGridViewTextBoxColumn
            {
                Name = "colPicked",
                HeaderText = "Picked",
                ReadOnly = true,
                Width = 150
            };
            dgvPickedFpts.Columns.Add(colPicked);

            // sr 컬럼 추가 (유사도)
            var colSr = new DataGridViewTextBoxColumn
            {
                Name = "colSr",
                HeaderText = "sr",
                ReadOnly = true,
                Width = 70
            };
            dgvPickedFpts.Columns.Add(colSr);

            // mt 컬럼 추가 (매칭 시간)
            var colMt = new DataGridViewTextBoxColumn
            {
                Name = "colMt",
                HeaderText = "mt",
                ReadOnly = true,
                Width = 90
            };
            dgvPickedFpts.Columns.Add(colMt);

            // dt 컬럼 추가 (소요 시간)
            var colDt = new DataGridViewTextBoxColumn
            {
                Name = "colDt",
                HeaderText = "dt",
                ReadOnly = true,
                Width = 80
            };
            dgvPickedFpts.Columns.Add(colDt);

            // verdict 컬럼 추가 (판정)
            var colResult = new DataGridViewTextBoxColumn
            {
                Name = "colVerdict",
                HeaderText = "판정",
                ReadOnly = true,
                Width = 80
            };
            dgvPickedFpts.Columns.Add(colResult);

            // 체크셋 항목 개수에 따라 FS+# 컬럼 추가
            UpdatePickedFeaturesColumns();
        }

        /// <summary>
        /// dgvPickedFeatures의 컨텍스트 메뉴 초기화
        /// </summary>
        private void InitializePickedFeaturesContextMenu()
        {
            if (dgvPickedFpts == null)
            {
                return;
            }

            var contextMenu = new ContextMenuStrip();
            
            // 삭제 메뉴 항목 추가
            var deleteMenuItem = new ToolStripMenuItem("삭제");
            deleteMenuItem.Click += DeletePickedFeatureMenuItem_Click;
            contextMenu.Items.Add(deleteMenuItem);
            
            dgvPickedFpts.ContextMenuStrip = contextMenu;
        }

        /// <summary>
        /// 삭제 메뉴 항목 클릭 이벤트 핸들러
        /// </summary>
        private void DeletePickedFeatureMenuItem_Click(object sender, EventArgs e)
        {
            if (dgvPickedFpts == null || dgvPickedFpts.SelectedRows.Count == 0)
            {
                return;
            }

            // 삭제 확인
            DialogResult result = MessageBox.Show(
                "선택된 항목을 삭제하시겠습니까?\n파일도 함께 삭제됩니다.",
                "삭제 확인",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result != DialogResult.Yes)
            {
                return;
            }

            // 선택된 행 삭제 및 파일 삭제
            // 역순으로 삭제하여 인덱스 문제 방지
            var rowsToDelete = dgvPickedFpts.SelectedRows.Cast<DataGridViewRow>().ToList();
            
            foreach (DataGridViewRow row in rowsToDelete)
            {
                // Tag에 저장된 파일 경로 가져오기
                string filePath = row.Tag as string;
                
                // Tag가 없거나 비어있으면 colPicked에서 파일명 가져와서 경로 구성
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    if (dgvPickedFpts.Columns.Contains("colPicked"))
                    {
                        var cell = row.Cells["colPicked"];
                        if (cell != null && cell.Value != null)
                        {
                            string fileName = cell.Value.ToString();
                            // featureDir와 파일명을 조합하여 전체 경로 생성
                            if (!string.IsNullOrWhiteSpace(_fptFile.featureDir) && 
                                Directory.Exists(_fptFile.featureDir))
                            {
                                filePath = Path.Combine(_fptFile.featureDir, fileName);
                            }
                        }
                    }
                }
                
                // 파일 삭제
                if (!string.IsNullOrWhiteSpace(filePath))
                {
                    // 상대 경로인 경우 절대 경로로 변환
                    if (!Path.IsPathRooted(filePath) && 
                        !string.IsNullOrWhiteSpace(_fptFile.featureDir) && 
                        Directory.Exists(_fptFile.featureDir))
                    {
                        filePath = Path.Combine(_fptFile.featureDir, filePath);
                    }
                    
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            File.Delete(filePath);
                            System.Diagnostics.Debug.WriteLine($"파일 삭제 완료: {filePath}");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"파일 삭제 실패: {filePath}, 오류: {ex.Message}");
                            // 파일 삭제 실패해도 행은 삭제 (사용자에게 알림은 선택적)
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"파일이 존재하지 않음: {filePath}");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"파일 경로를 찾을 수 없음. Tag: {row.Tag}, colPicked: {(row.Cells["colPicked"]?.Value?.ToString() ?? "null")}");
                }
                
                // 행 삭제
                dgvPickedFpts.Rows.Remove(row);
            }
            
            // 행 번호 재정렬
            for (int i = 0; i < dgvPickedFpts.Rows.Count; i++)
            {
                if (dgvPickedFpts.Columns.Contains("colNumber"))
                {
                    dgvPickedFpts.Rows[i].Cells["colNumber"].Value = (i + 1).ToString();
                }
            }
        }

        /// <summary>
        /// dgvPickedFeatures CellMouseDown 이벤트 핸들러
        /// 마우스 오른쪽 클릭 시 해당 행을 선택합니다.
        /// </summary>
        private void DgvPickedFeatures_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (dgvPickedFpts == null || e.RowIndex < 0)
            {
                return;
            }

            // 마우스 오른쪽 클릭인 경우
            if (e.Button == MouseButtons.Right)
            {
                // 해당 행이 선택되어 있지 않으면 선택
                if (!dgvPickedFpts.Rows[e.RowIndex].Selected)
                {
                    // 기존 선택 해제
                    dgvPickedFpts.ClearSelection();
                    // 해당 행 선택
                    dgvPickedFpts.Rows[e.RowIndex].Selected = true;
                }
            }
        }

        private void DgvPickedFpts_KeyDown(object sender, KeyEventArgs e)
        {
            if (dgvPickedFpts == null || dgvPickedFpts.SelectedRows.Count == 0)
            {
                return;
            }

            if (e.KeyCode == Keys.Delete)
            {
                DeletePickedFeatureMenuItem_Click(sender, EventArgs.Empty);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                // Enter 키: DoubleClick과 동일한 동작 수행
                int selectedRowIndex = dgvPickedFpts.SelectedRows[0].Index;
                if (selectedRowIndex >= 0)
                {
                    ProcessSelectedFingerprint(selectedRowIndex);
                }
                e.Handled = true;
            }
        }

        /// <summary>
        /// dgvPickedFeatures의 FS+# 컬럼을 업데이트합니다.
        /// 각 FS+#에 대해 3개의 컬럼(FS#F: 찾은시간, FS#S: 유사도, FS#D: 소요시간)을 생성
        /// </summary>
        private void UpdatePickedFeaturesColumns()
        {
            if (dgvPickedFpts == null)
            {
                return;
            }

            int checkSetCount = _checksetHandler.checkSet.items.Count;
            int currentFsColumnCount = 0;

            // 현재 FS+# 컬럼 개수 확인 (colNumber, colPicked 제외)
            // FS#F, FS#S, FS#D가 하나의 세트이므로 F로 끝나는 컬럼의 개수를 세어서 확인
            var fsFColumns = new HashSet<int>();
            for (int i = dgvPickedFpts.Columns.Count - 1; i >= 0; i--)
            {
                var col = dgvPickedFpts.Columns[i];
                if (col.Name.StartsWith("colFS") && col.Name.EndsWith("F"))
                {
                    // colFS#F 형식에서 숫자 추출
                    string numberPart = col.Name.Substring(5, col.Name.Length - 6); // "colFS"와 "F" 제거
                    if (int.TryParse(numberPart, out int fsNumber))
                    {
                        fsFColumns.Add(fsNumber);
                    }
                }
            }
            currentFsColumnCount = fsFColumns.Count > 0 ? fsFColumns.Max() : 0;

            // 필요한 경우 FS+# 컬럼 추가 (각 FS+#에 대해 F, S, D, V 4개 컬럼)
            if (checkSetCount > currentFsColumnCount)
            {
                for (int i = currentFsColumnCount + 1; i <= checkSetCount; i++)
                {
                    // FS#F 컬럼 (찾은 시간)
                    var colFsF = new DataGridViewTextBoxColumn
                    {
                        Name = $"colFS{i}F",
                        HeaderText = $"{i}F",
                        ReadOnly = true,
                        Width = 80
                    };
                    dgvPickedFpts.Columns.Add(colFsF);

                    // FS#S 컬럼 (유사도)
                    var colFsS = new DataGridViewTextBoxColumn
                    {
                        Name = $"colFS{i}S",
                        HeaderText = $"{i}S",
                        ReadOnly = true,
                        Width = 80
                    };
                    dgvPickedFpts.Columns.Add(colFsS);

                    // FS#D 컬럼 (소요 시간)
                    var colFsD = new DataGridViewTextBoxColumn
                    {
                        Name = $"colFS{i}D",
                        HeaderText = $"{i}D",
                        ReadOnly = true,
                        Width = 80
                    };
                    dgvPickedFpts.Columns.Add(colFsD);

                    // FS#V 컬럼 (판정)
                    var colFsV = new DataGridViewTextBoxColumn
                    {
                        Name = $"colFS{i}V",
                        HeaderText = $"{i}V",
                        ReadOnly = true,
                        Width = 80
                    };
                    dgvPickedFpts.Columns.Add(colFsV);
                }
            }
            // 불필요한 경우 FS+# 컬럼 제거 (F, S, D, V 모두 제거)
            else if (checkSetCount < currentFsColumnCount)
            {
                for (int i = currentFsColumnCount; i > checkSetCount; i--)
                {
                    string[] colNames = { $"colFS{i}F", $"colFS{i}S", $"colFS{i}D", $"colFS{i}V" };
                    foreach (string colName in colNames)
                    {
                        if (dgvPickedFpts.Columns.Contains(colName))
                        {
                            dgvPickedFpts.Columns.Remove(colName);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// btnPickSnipet 클릭 이벤트 핸들러 - 재생 중인 오디오의 핑거프린트를 저장
        /// </summary>
        private async void BtnPickSnipet_Click(object sender, EventArgs e)
        {
            try
            {
                // 재생 중인 오디오 확인
                if (_audioFileReader == null)
                {
                    MessageBox.Show("재생 중인 오디오가 없습니다.", "오디오 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                //pa.workTab.pickParam.adaptiveDynamic = chkAdaptiveDynamic.Checked ? true : false;
                pa.workTab.pickParam.pickTime = _audioFileReader.CurrentTime;
                // 현재 재생 시간 가져오기 (스냅샷)
                //TimeSpan pickTime = _audioFileReader.CurrentTime;

                // term 값 설정
                // 콤보박스에서 선택한 ms 값 사용, 선택이 없으면 프로필에서 읽거나 기본값 3000ms
                int termMs = 10000;
                if (cboSnippetTermMs != null && cboSnippetTermMs.SelectedItem != null)
                {
                    if (!int.TryParse(cboSnippetTermMs.SelectedItem.ToString(), out termMs))
                    {
                        termMs = GetSnippetLengthFromProfile();
                    }
                }
                else
                {
                    // 선택이 없으면 프로필에서 읽기 시도
                    termMs = GetSnippetLengthFromProfile();
                }

                // 버튼 비활성화 (중복 클릭 방지)
                btnPickSnipet.Enabled = false;

                // txtTimePicked와 트랙바 업데이트가 계속되도록 보장
                // _isUserEditingTime을 false로 유지하여 PlayProgressTimer가 계속 업데이트하도록 함
                _isUserEditingTime = false;

                try
                {
                    // 핑거프린트 생성 및 저장 (비동기로 실행하여 UI가 멈추지 않도록 함)
                    bool success = await SavePickedFingerprint(pa.workTab.pickParam, termMs);
                    //if (success) {
                    //    MessageBox.Show($"핑거프린트가 저장되었습니다.\n" + $"시간: {pickTime:hh\\:mm\\:ss\\.fff}\n" + $"구간: {termMs}ms",
                    //        "저장 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    //}
                }
                finally
                {
                    // 버튼 다시 활성화
                    btnPickSnipet.Enabled = true;
                    // _isUserEditingTime을 false로 유지하여 계속 업데이트되도록 함
                    _isUserEditingTime = false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"핑거프린트 저장 중 오류가 발생했습니다.\n{ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                // 오류 발생 시에도 업데이트가 계속되도록 보장
                _isUserEditingTime = false;
            }
        }

        /// <summary>
        /// btnFlagMic MouseDown 이벤트 핸들러 - 마이크/Loopback 입력을 WAV 파일로 녹음/중지
        /// - 일반 클릭: 마이크 녹음
        /// - Ctrl+클릭: Loopback 녹음 (시스템 오디오 출력 캡처)
        /// 파일명 형식: MicAudio_일시분초.wav 또는 Loopback_일시분초.wav
        /// </summary>
        private void BtnFlagMic_MouseDown(object sender, MouseEventArgs e)
        {
            if (!_isMicRecording)
            {
                StartMicOrLoopbackRecording();
            }
            else
            {
                StopMicOrLoopbackRecording();
            }
        }

        /// <summary>
        /// 마이크/Loopback 녹음 시작
        /// </summary>
        /// <returns>녹음 시작 성공 여부</returns>
        private bool StartMicOrLoopbackRecording()
        {
            if (_isMicRecording)
                return false; // 이미 녹음 중

            try
            {
                _isLoopbackMode = (chkLoopbackRec.Checked) ? true : false;
                
                if (!_isLoopbackMode)
                {
                    // 마이크 장치 존재 여부 확인
                    if (WaveIn.DeviceCount <= 0)
                    {
                        MessageBox.Show("사용 가능한 마이크 장치가 없습니다.", "마이크 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }
                }

                // 저장 폴더 결정: 영화 오디오 디렉터리가 있으면 그곳, 없으면 실행 폴더
                string targetDir = null;
                if (!string.IsNullOrWhiteSpace(_fptFile.movieFolder) && Directory.Exists(_fptFile.movieFolder))
                {
                    targetDir = _fptFile.movieFolder;
                }
                else
                {
                    targetDir = Application.StartupPath;
                }

                // 파일명: MicAudio_HHmmss.wav 또는 Loopback_HHmmss.wav
                string prefix = _isLoopbackMode ? "Loopback" : "MicAudio";
                string fileName = $"{prefix}_{DateTime.Now:HHmmss}.wav";
                string filePath = Path.Combine(targetDir, fileName);

                // txtSustainTime에서 녹음 지속 시간 읽기
                _micRecordingDuration = HHmmssToTimeSpan(txtSustainTime.Text);
                // 녹음 시작 시간 기록
                _micRecordingStartTime = DateTime.Now;

                if (_isLoopbackMode)
                {
                    // ★★★ Loopback 모드: 시스템 오디오 출력 캡처 ★★★
                    _loopbackCapture = new NAudio.Wave.WasapiLoopbackCapture();
                    
                    // ★★★ 2026-02-02 수정: Loopback 캡처의 실제 샘플레이트 사용 ★★★
                    // 문제: 48000Hz로 고정하면 시스템이 44100Hz일 때 피치/속도 변경
                    // 해결: Loopback 캡처의 실제 샘플레이트로 WAV 저장
                    int loopbackSampleRate = _loopbackCapture.WaveFormat.SampleRate;
                    var targetFormat = new WaveFormat(loopbackSampleRate, 16, 1);
                    _micWriter = new WaveFileWriter(filePath, targetFormat);
                    
                    System.Diagnostics.Debug.WriteLine($"[Loopback] 원본 형식: {_loopbackCapture.WaveFormat}");
                    System.Diagnostics.Debug.WriteLine($"[Loopback] 저장 형식: {targetFormat} (샘플레이트: {loopbackSampleRate}Hz)");
                    
                    _loopbackCapture.DataAvailable += (s, args) =>
                    {
                        if (_isMicRecording && _micWriter != null)
                        {
                            // IEEE Float → 16bit PCM 모노 변환 (샘플레이트 유지)
                            byte[] convertedData = ConvertLoopbackTo16BitMono(args.Buffer, args.BytesRecorded, _loopbackCapture.WaveFormat);
                            if (convertedData != null && convertedData.Length > 0)
                            {
                                _micWriter.Write(convertedData, 0, convertedData.Length);
                            }
                        }
                    };
                    
                    _loopbackCapture.RecordingStopped += (s, args) =>
                    {
                        CleanupMicRecording();
                    };
                    
                    // 녹음 시작
                    _isMicRecording = true;
                    _loopbackCapture.StartRecording();
                }
                else
                {
                    // ★★★ 마이크 모드: 기존 로직 ★★★
                    _micWaveIn = new WaveInEvent
                    {
                        WaveFormat = new WaveFormat(48000, 16, 1), // 48kHz, 16bit, 모노
                        BufferMilliseconds = 100
                    };

                    _micWriter = new WaveFileWriter(filePath, _micWaveIn.WaveFormat);

                    _micWaveIn.DataAvailable += (s, args) =>
                    {
                        if (_isMicRecording && _micWriter != null)
                        {
                            byte[] outBuffer = AudioVolumeGain(args.Buffer, 16.0f);
                            _micWriter.Write(outBuffer, 0, outBuffer.Length);
                        }
                    };

                    _micWaveIn.RecordingStopped += (s, args) =>
                    {
                        CleanupMicRecording();
                    };

                    // 녹음 시작
                    _isMicRecording = true;
                    _micWaveIn.StartRecording();
                }

                // 자동 종료 타이머 시작 (지속 시간이 설정된 경우에만)
                if (_micRecordingDuration.TotalSeconds > 0)
                {
                    _micRecordingTimer.Start();
                }

                // 버튼 상태 변경 (녹음 중 표시)
                btnFlagMic.Text = _isLoopbackMode ? "Loop" : "MicRec";
                btnFlagMic.BackColor = _isLoopbackMode 
                    ? Color.FromArgb(200, 100, 100, 255)  // 파란색 계통 (Loopback)
                    : Color.FromArgb(200, 255, 100, 100); // 붉은색 계통 (마이크)

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"마이크 녹음 중 오류가 발생했습니다.\n{ex.Message}", "마이크 녹음 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                StopMicRecording();
                btnFlagMic.Text = "mic";
                UpdateMicFlag();
                return false;
            }
        }

        /// <summary>
        /// 마이크/Loopback 녹음 중지 및 버튼 상태 복원
        /// </summary>
        private void StopMicOrLoopbackRecording()
        {
            // 녹음 중지
            StopMicRecording();

            // 버튼 상태 원복
            btnFlagMic.Text = "mic";
            UpdateMicFlag(); // 장치 상태에 따라 색상 복원
        }

        private TimeSpan HHmmssToTimeSpan(string inputTime)
        {
            // HH:mm:ss 형식 파싱
            if (TimeSpan.TryParse(inputTime, out TimeSpan parsedDuration))
            {
                return parsedDuration;
            }
            else
            {
                // 파싱 실패 시 숫자만 추출하여 초로 해석
                string numbersOnly = new string(inputTime.Where(char.IsDigit).ToArray());
                if (!string.IsNullOrEmpty(numbersOnly) && long.TryParse(numbersOnly, out long totalSeconds))
                {
                    return TimeSpan.FromSeconds(totalSeconds);
                }
            }
            return TimeSpan.Zero;
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="argsBuffer"></param>
        /// <param name="gain"></param>
        /// <returns></returns>
        private byte[] AudioVolumeGain(byte[] argsBuffer, float gain = 4.0f)
        { 
            // 16bit PCM 샘플에 게인 적용 (예: 4배, 클리핑 방지)
            //const float gain = 4.0f; // 필요 시 조정 
            int argsBytesRecorded = argsBuffer.Length;
            int bytesPerSample = 2; // 16bit
            int sampleCount = argsBytesRecorded / bytesPerSample;

            // 원본 버퍼를 그대로 쓰지 말고, 증폭된 샘플을 새 버퍼에 기록
            byte[] outBuffer = new byte[argsBytesRecorded];

            for (int i = 0; i < sampleCount; i++)
            {
                int offset = i * bytesPerSample;

                short sample = BitConverter.ToInt16(argsBuffer, offset);
                int amplified = (int)(sample * gain);

                if (amplified > short.MaxValue) amplified = short.MaxValue;
                if (amplified < short.MinValue) amplified = short.MinValue;

                short outSample = (short)amplified;
                outBuffer[offset] = (byte)(outSample & 0xFF);
                outBuffer[offset + 1] = (byte)((outSample >> 8) & 0xFF);
            }
            return outBuffer;
        }
        /// <summary>
        /// 마이크 녹음 자동 종료 타이머 Tick 이벤트 핸들러
        /// </summary>
        private void MicRecordingTimer_Tick(object sender, EventArgs e)
        {
            if (!_isMicRecording)
            {
                _micRecordingTimer.Stop();
                return;
            }

            // 경과 시간 확인
            TimeSpan elapsed = DateTime.Now - _micRecordingStartTime;
            
            // 설정된 지속 시간을 초과하면 자동 종료
            if (elapsed >= _micRecordingDuration)
            {
                _micRecordingTimer.Stop();
                StopMicRecording();
                
                // 버튼 상태 원복
                if (btnFlagMic != null)
                {
                    btnFlagMic.Text = "mic";
                    UpdateMicFlag();
                }
            }
        }

        /// <summary>
        /// 마이크 녹음을 중지합니다.
        /// </summary>
        private void StopMicRecording()
        {
            // 타이머 중지
            _micRecordingTimer?.Stop();
            
            if (_isMicRecording)
            {
                try
                {
                    _isMicRecording = false;
                    
                    // ★★★ 2026-02-02: Loopback/마이크 모드에 따라 중지 ★★★
                    if (_isLoopbackMode)
                    {
                        _loopbackCapture?.StopRecording();
                    }
                    else
                    {
                        _micWaveIn?.StopRecording();
                    }
                }
                catch
                {
                    // 무시
                }
            }

            CleanupMicRecording();
        }

        private long _totalSamplesProcessed = 0; // 정확한 타임스탬프 계산을 위한 누적 샘플 수

        /// <summary>
        /// 마이크 녹음 관련 리소스 정리
        /// </summary>
        private void CleanupMicRecording()
        {
            try
            {
                if (_micWaveIn != null)
                {
                    _micWaveIn.Dispose();
                    _micWaveIn = null;
                }

                // ★★★ 2026-02-02: Loopback 캡처 정리 추가 ★★★
                if (_loopbackCapture != null)
                {
                    _loopbackCapture.Dispose();
                    _loopbackCapture = null;
                }

                if (_micWriter != null)
                {
                    _micWriter.Dispose();
                    _micWriter = null;
                }
            }
            catch
            {
                // 정리 중 예외는 무시
            }
            finally
            {
                _isMicRecording = false;
                _isLoopbackMode = false;
            }
        }

        /// <summary>
        /// Loopback 캡처 데이터 (IEEE Float Stereo) → 16bit PCM Mono 변환
        /// </summary>
        private byte[] ConvertLoopbackTo16BitMono(byte[] buffer, int bytesRecorded, WaveFormat sourceFormat)
        {
            try
            {
                // IEEE Float (32bit) 스테레오 → 16bit PCM 모노
                int sourceSampleSize = sourceFormat.BitsPerSample / 8; // 4 (float)
                int sourceChannels = sourceFormat.Channels; // 2 (stereo)
                int sourceBytesPerSample = sourceSampleSize * sourceChannels; // 8
                
                int sampleCount = bytesRecorded / sourceBytesPerSample;
                byte[] outputBuffer = new byte[sampleCount * 2]; // 16bit mono = 2 bytes per sample
                
                for (int i = 0; i < sampleCount; i++)
                {
                    int sourceOffset = i * sourceBytesPerSample;
                    
                    // 스테레오 채널 평균 (IEEE Float)
                    float left = BitConverter.ToSingle(buffer, sourceOffset);
                    float right = sourceChannels > 1 
                        ? BitConverter.ToSingle(buffer, sourceOffset + sourceSampleSize) 
                        : left;
                    float mono = (left + right) / 2.0f;
                    
                    // Float → 16bit PCM (클리핑 방지)
                    mono = Math.Max(-1.0f, Math.Min(1.0f, mono));
                    short sample16 = (short)(mono * 32767);
                    
                    // 출력 버퍼에 쓰기
                    outputBuffer[i * 2] = (byte)(sample16 & 0xFF);
                    outputBuffer[i * 2 + 1] = (byte)((sample16 >> 8) & 0xFF);
                }
                
                return outputBuffer;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Loopback Convert Error] {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// btnLivePickSnipet 클릭 이벤트 핸들러 - 실시간 핑거프린트 매칭 시작/중지
        /// </summary>
        private void BtnLivePickSnipet_Click(object sender, EventArgs e)
        {
            try
            {
                if (!_isMatching)
                {
                    // 기준 핑거프린트가 로드되지 않았다면 경고
                    var fptFilePath = _fptFile.GetFeatureFilePath();
                    if (string.IsNullOrWhiteSpace(fptFilePath) || !File.Exists(fptFilePath))
                    {
                        UpdatePickStatusMessage("오류: 영화 핑거프린트 파일 없음");
                        //MessageBox.Show("영화 핑거프린트 파일이 없습니다.\n먼저 핑거프린트를 생성하거나 로드해 주세요.", "핑거프린트 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // 역인덱스가 로드되지 않았다면 경고
                    if (_movieRvsIndex == null || _movieRvsIndex.Count == 0)
                    {
                        UpdatePickStatusMessage("오류: 영화 핑거프린트 로드 필요");
                        //MessageBox.Show("영화 핑거프린트가 로드되지 않았습니다.\n먼저 핑거프린트를 로드해 주세요.", "핑거프린트 미로드", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    // 안정 매칭 이벤트함수 연결
                    StableMatchFound += MatchingService_StableMatchFound;

                    // ★★★ 2026-02-05: txtTimePlay 값을 txtTimeTry에 복사 ★★★
                    if (txtTimePlay != null && txtTimeTry != null)
                    {
                        txtTimeTry.Text = txtTimePlay.Text;
                    }

                    // 실시간 매칭 시작
                    UpdatePickStatusMessage("🎤 실시간 매칭 시작...");
                    StartMatching();
                    _isMatching = true;

                    // 버튼 상태 변경
                    btnLivePickSnipet.Text = "Live Stop";
                    btnLivePickSnipet.BackColor = Color.FromArgb(150, 100, 200, 100); // 녹색계통
                    UpdatePickStatusMessage($"🎤 실시간 매칭 중... (샘플레이트: {pa.workTab.pickParam.sampleRate}Hz)");
                }
                else
                {
                    // 실시간 매칭 중지
                    StopMatching();
                    StableMatchFound -= MatchingService_StableMatchFound;
                    _isMatching = false;
                    
                    // 디버그 WAV 파일 닫기
                    if (_debugWriter != null)
                    {
                        _debugWriter.Dispose();
                        _debugWriter = null;
                        System.Diagnostics.Debug.WriteLine("[SimTest] Debug WAV file saved.");
                    }

                    // 버튼 상태 복원
                    btnLivePickSnipet.Text = "Live Pick";
                    btnLivePickSnipet.BackColor = SystemColors.ButtonFace;
                    UpdatePickStatusMessage("실시간 매칭 중지됨");
                    
                    // 리소스 정리 (삭제된 File Injection 코드 정리 불필요)
                }
            }
            catch (Exception ex)
            {
                UpdatePickStatusMessage($"오류: {ex.Message}");
                MessageBox.Show($"실시간 매칭 중 오류가 발생했습니다.\n{ex.Message}", "실시간 매칭 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 실시간 매칭 시작
        /// </summary>
        public void StartMatching()
        {
            if (_matcher == null)
            {
                throw new InvalidOperationException("먼저 LoadReferenceFingerprint를 호출하세요.");
            }

            _isMatching = true;
            _audioBuffer = new List<float>();
            _prevAudioTail = null; // ★ 오버랩 버퍼 초기화 ★
            _cumulativeRecordingSeconds = 0;  // ★ 누적 녹음 시간 초기화 ★
            _totalSamplesProcessed = 0; // ★ 누적 샘플 수 초기화 ★
            _consecutiveLowFeatureCount = 0;  // ★ 저특징 구간 카운트 초기화 ★
            
            // ★★★ 2026-02-03: 원본 FPT와 동일한 설정 적용 ★★★
            // 문제: Live에서 minMagnitude=0.1 → 일부 프레임 건너뜀 → 해시 불일치!
            // 해결: 원본과 동일하게 필터링 없이 모든 프레임 처리
            pa.workTab.pickParam.UseQualityBasedFiltering = false;  // 원본과 동일
            pa.workTab.pickParam.QualityThreshold = 0.0;            // 원본과 동일
            pa.workTab.pickParam.minMagnitude = 0.0;                // ★ 핵심: 원본과 동일하게 0.0 ★
            
            // ★ 타겟 해시 밀도 제한 (과다 생성 방지) ★
            // 초당 100개 목표 (현재 340개로 너무 많음)
            pa.workTab.pickParam.targetHashesPerSec = 100;

            
            // HPF 초기화
            _hpfPrevIn = 0f;
            _hpfPrevOut = 0f;
            
            // 디버그 WAV 파일 생성 (실행 파일 폴더에 저장)
            try
            {
                string debugPath = Path.Combine(Application.StartupPath, "debug_live_capture.wav");
                // ★ 중요: WriteSamples(float[])를 사용하려면 반드시 IeeeFloat 포맷이어야 함 ★
                _debugWriter = new WaveFileWriter(debugPath, NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(pa.workTab.pickParam.sampleRate, 1)); 
            }
            catch (Exception ex)
            { 
                System.Diagnostics.Debug.WriteLine($"[DebugWriter Init Error] {ex.Message}");
                _debugWriter = null; 
            }

            // 오디오 캡처 시작
            int targetSampleRate = pa.workTab.pickParam.sampleRate;
            System.Diagnostics.Debug.WriteLine($"[StartRecording] SampleRate: {targetSampleRate}");

            // ★★★ 2026-02-02: 리샘플링 제거 - 캡처 샘플레이트를 타겟과 동일하게 ★★★
            // 문제: 44100Hz → 48000Hz Linear Interpolation 리샘플링이 해시 불일치 원인
            // 해결: 캡처 샘플레이트를 pickParam.sampleRate와 동일하게 설정
            // 주의: 마이크/사운드카드가 해당 샘플레이트를 지원해야 함
            int captureSampleRate = targetSampleRate; // 리샘플링 제거! 

            try
            {
                _waveIn = new NAudio.Wave.WaveInEvent
                {
                    // 16bit Mono, Capture at 44100
                    WaveFormat = new NAudio.Wave.WaveFormat(captureSampleRate, 16, 1), 
                    BufferMilliseconds = 100
                };
                System.Diagnostics.Debug.WriteLine($"[StartMatching] Mic: {captureSampleRate}Hz -> Will Resample to {targetSampleRate}Hz. Gain x1.0 (원본 FPT와 동일).");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"마이크 초기화 실패: {ex.Message}");
                return;
            }
            
             _expectTime = TimeSpan.Parse(txtTimePlay.Text);

            // ★★★ 2026-02-06: 마이크 캘리브레이션 시작 (MicCalibrator 사용) ★★★
            _micCalibrator.Start(captureSampleRate);
            UpdatePickStatusMessage("🎤 마이크 캘리브레이션 중... (2초)");
            
            // ★★★ 2026-02-07: 스트리밍 전처리기 초기화 (윈도우 간 연속성 보장) ★★★
            _streamingPreprocessor = new StreamingPreprocessor(targetSampleRate, pa.workTab.pickParam.PP);
            
            // 이벤트 핸들러 연결
            _waveIn.DataAvailable -= OnAudioDataAvailable;
            _waveIn.DataAvailable += OnAudioDataAvailable;
            _waveIn.StartRecording();
        }
        
        /// <summary>
        /// 파일 주입 시뮬레이션 타이머 핸들러
        /// </summary>



        /// <summary>
        /// 오디오 데이터 수신 이벤트 핸들러 (WaveInEvent, 16bit PCM + Gain x16 + Resampling + HPF)
        /// </summary>
        private void OnAudioDataAvailable(object sender, NAudio.Wave.WaveInEventArgs e)
        {
            if (!_isMatching) return;

            int sampleRate = pa.workTab.pickParam.sampleRate; // 48000
            int sourceRate = _waveIn.WaveFormat.SampleRate;   // 44100 (예상)

            // ★ 1. Byte to Float Conversion ★
            int bytesRecorded = e.BytesRecorded;
            int samplesCount = bytesRecorded / 2;
            
            float[] inputChunk = new float[samplesCount];
            for (int i = 0; i < samplesCount; i++)
            {
                short sampleShort = BitConverter.ToInt16(e.Buffer, i * 2);
                inputChunk[i] = sampleShort / 32768f;
            }
            
            // ★★★ 2026-02-07: 마이크 캘리브레이션 비활성화 (테스트: 원본 FPT와 동일 조건) ★★★
            // 문제: 게인 적용 시 원본 FPT와 해시 불일치 발생
            // 테스트: 캘리브레이션/게인 없이 Raw 데이터로 처리
            /*
            if (_micCalibrator.IsCalibrating)
            {
                bool completed = _micCalibrator.ProcessSamples(inputChunk);
                if (completed)
                {
                    UpdatePickStatusMessage($"🎤 캘리브레이션 완료 (게인: {_micCalibrator.CalibratedGain:F1}x) - 매칭 시작");
                }
                return; // 캘리브레이션 중에는 매칭 처리하지 않음
            }
            
            // ★★★ 캘리브레이션된 게인 적용 (MicCalibrator 사용) ★★★
            _micCalibrator.ApplyGain(inputChunk);
            */

            // ★ 2. Resampling (Linear Interpolation) if needed ★
            float[] processingChunk;
            if (sourceRate != sampleRate)
            {
                float ratio = (float)sourceRate / sampleRate;
                int outputCount = (int)(samplesCount / ratio);
                processingChunk = new float[outputCount];

                for (int i = 0; i < outputCount; i++)
                {
                    float srcPos = i * ratio;
                    int idx0 = (int)srcPos;
                    if (idx0 >= samplesCount) idx0 = samplesCount - 1; // Safety clamp
                    int idx1 = idx0 + 1;
                    if (idx1 >= samplesCount) idx1 = samplesCount - 1;
                    float t = srcPos - idx0;

                    float s0 = inputChunk[idx0];
                    float s1 = inputChunk[idx1];
                    processingChunk[i] = (1f - t) * s0 + t * s1;
                }
            }
            else
            {
                processingChunk = inputChunk;
            }

            // ★ 3. HighPass Filter (DC Blocking / Low Cut) ★
            // ★★★ 2026-02-02: HPF 비활성화 테스트 ★★★
            // 문제: 임의 위치 매칭에는 HPF가 없음 → 주파수 특성 차이 발생 가능
            // 테스트: HPF 비활성화하여 임의 위치 매칭과 동일한 조건으로
            /*
            float alpha = 0.99f;
            for (int i = 0; i < processingChunk.Length; i++)
            {
                float input = processingChunk[i];
                float output = alpha * (_hpfPrevOut + input - _hpfPrevIn);
                _hpfPrevIn = input;
                _hpfPrevOut = output;
                processingChunk[i] = output;
            }
            */

            lock (_audioBufferLock)
            {
                _audioBuffer.AddRange(processingChunk);
            }

            // 1초 분량 데이터 체크
            bool shouldProcess = false;
            lock (_audioBufferLock)
            {
                if (_audioBuffer.Count >= sampleRate) shouldProcess = true;
            }
            
            if (shouldProcess)
            {
                ProcessAudioBuffer();
            }
        }
        /// <summary>
        /// 오디오 버퍼 처리 및 매칭 수행
        /// </summary>
        private void ProcessAudioBuffer()
        {
            var pickParam = pa.workTab.pickParam;
            int sampleRate = pickParam.sampleRate;
            
            // ★★★ 2026-02-07: 샘플 기반 슬라이드 트리거 ★★★
            // 기존: HopSize 배수 (47104 샘플 ≈ 0.981초) → 불일치 발생
            // 변경: 정확히 sampleRate 샘플 = 1초 (Single Source of Truth)
            int targetSamples = sampleRate; // 정확히 1초 = sampleRate 샘플

            if (_audioBuffer.Count < targetSamples) return; // 데이터 부족 시 대기

            float[] audioChunk = _audioBuffer.Take(targetSamples).ToArray();
            _audioBuffer.RemoveRange(0, targetSamples);

            // ★★★ 2026-02-07: 오디오 샘플 기반 Timestamp (Single Source of Truth) ★★★
            // _totalSamplesProcessed를 유일한 시간 기준으로 사용
            // Timestamp = floor(totalSamples / sampleRate)
            int currentTimestamp = (int)(_totalSamplesProcessed / sampleRate); // 현재 슬라이드의 초 단위 타임스탬프
            int cumulativeMs = (int)(_totalSamplesProcessed * 1000L / sampleRate); // 밀리초 단위
            _totalSamplesProcessed += audioChunk.Length; // 카운트 증가 (슬라이드 후)
            _cumulativeRecordingSeconds = (int)(_totalSamplesProcessed / sampleRate); // ★ 샘플 기반 동기화 ★

            // ★ 오디오 버퍼 오버랩 처리 확장 (Window Size 확보) ★
            // 기존 0.5초 오버랩 -> 3초(HashTimeWindow) 이상 확보
            // 이렇게 해야 1.5초~3초 간격의 Peak 쌍도 놓치지 않고 해시 생성 가능
            double overlapSeconds = 3.0; 
            int overlapSamples = (int)(sampleRate * overlapSeconds); 
            
            float[] processingChunk;

            if (_prevAudioTail == null)
            {
                // 첫 버퍼는 앞부분 오버랩 없음 (0 패딩 효과)
                processingChunk = audioChunk;
            }
            else
            {
                // 이전 뒷부분(3초) + 현재 청크(약 1초) = 약 4초 윈도우
                processingChunk = new float[_prevAudioTail.Length + audioChunk.Length];
                Array.Copy(_prevAudioTail, 0, processingChunk, 0, _prevAudioTail.Length);
                Array.Copy(audioChunk, 0, processingChunk, _prevAudioTail.Length, audioChunk.Length);
            }
            
            // 현재 버퍼의 뒷부분을 저장 (다음 처리를 위해)
            // 다음 윈도우를 위해서 현재 처리한 긴 윈도우의 끝부분 3초를 남겨둠
            // (processingChunk 자체에서 추출하면 더 간단함)
            if (processingChunk.Length >= overlapSamples)
            {
                _prevAudioTail = new float[overlapSamples];
                Array.Copy(processingChunk, processingChunk.Length - overlapSamples, _prevAudioTail, 0, overlapSamples);
            }
            else
            {
                _prevAudioTail = processingChunk; // 버퍼가 너무 짧으면 전체 저장
                
                // ★ Ramp-Up 기간: 버퍼가 충분히 차지 않았으면 스킵 ★
                // 3초 미만이면 유의미한 해시가 생성되지 않으므로 처리 생략 (노이즈 방지)
                System.Diagnostics.Debug.WriteLine($"[실시간 매칭] 버퍼 채우는 중... ({processingChunk.Length}/{overlapSamples})");
                return;
            }

            // ★ [신규] 어려운 구간 감지: RMS가 낮으면 건너뛰기 ★
            double rms = SFPFM.CalculateRMS(audioChunk); // 현재 들어온 1초에 대해서만 RMS 체크
            if (SFPFM.IsDifficultSegment(rms, isRealtime: true))  
            {
                System.Diagnostics.Debug.WriteLine($"[실시간 매칭] 어려운 구간 감지 (RMS:{rms:F4}) → 건너뛰기");
                UpdatePickStatusMessage($"🎤 오디오 수신 중... (약한 신호 건너뛰기, RMS:{rms:F4})");
                // 이미 카운트 증가했으므로 return만 하면 됨
                return; 
            }

            // ★ 전처리 재활성화: 라이브 오디오 품질(볼륨/노이즈) 개선 필요 ★
            // pickParam.sampleRate는 이미 targetSampleRate와 같음 (구조상)
            
            double gateSoftnessMultiplier = pickParam.gateSoftnessMultiplier;
            
            // 오프셋 집중도가 낮으면 더 부드러운 게이트 사용(선택적)
            if (_lastOffsetConcentration.HasValue && _lastOffsetConcentration.Value < pickParam.offsetConcntThreshold)
            {
                gateSoftnessMultiplier = pickParam.gateSoftnessMultiplierLowOffset;
            }
            
            // ★ FFT/Hop Size 검증 로그 (최초 1회) ★
            if (_totalSamplesProcessed <= targetSamples * 3) // 초반에만 로그
            {
                var cfg = pickParam.fptCfg;
                System.Diagnostics.Debug.WriteLine($"[Live Config Check] FFT:{cfg.FFTSize}, Hop:{cfg.HopSize}, SR:{pickParam.sampleRate}");
                System.Diagnostics.Debug.WriteLine($"[Live Window] Target: {targetSamples} smp, Overlap: {overlapSamples} smp, Current: {processingChunk.Length} smp");
                System.Diagnostics.Debug.WriteLine($"[Live Timestamp] currentTimestamp={currentTimestamp}s, cumulativeMs={cumulativeMs}ms, totalSamples={_totalSamplesProcessed}");
            }

            // ★ 디버그 WAV 저장 ★
            if (_debugWriter != null)
            {
                try
                {
                    // 디버그용으로는 현재 유입된 청크만 저장
                    _debugWriter.WriteSamples(audioChunk, 0, audioChunk.Length);
                    if (_debugWriter.Length > 48000 * 4 * 60) // 1분 넘으면 닫기
                    {
                        var writer = _debugWriter;
                        _debugWriter = null;
                        writer.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DebugWriter Error] {ex.Message}");
                    // 쓰기 실패 시 (Dispose됨 등) 무시하고 writer 해제
                    try { _debugWriter?.Dispose(); } catch { }
                    _debugWriter = null;
                }
            }

            Console.WriteLine($"[Live 시도] 상영 시간: {_expectTime} ");
            
            // ★★★ 2026-02-07: 스트리밍 전처리 적용 (윈도우 간 연속성 보장) ★★★
            // 기존: PreprocessSamplesForFingerprint (윈도우별 독립 처리 → 변동 발생)
            // 신규: StreamingPreprocessor (상태 유지 → 부드러운 변화)
            // ★★★ 2026-02-07: 스트리밍 전처리 비활성화 (FPT와 100% 동일 조건 테스트) ★★★
            // if (_streamingPreprocessor != null)
            // {
            //     processingChunk = _streamingPreprocessor.Process(processingChunk, gateSoftnessMultiplier);
            //     
            //     // 디버그: 전처리 상태 출력 (초반 3초만)
            //     if (_totalSamplesProcessed <= targetSamples * 3)
            //     {
            //         System.Diagnostics.Debug.WriteLine($"[StreamingPreprocessor] {_streamingPreprocessor.GetStatusInfo()}");
            //     }
            // }
            
            // 핑거프린트 생성 (약 4초 윈도우 사용)
            var rawFingerprints = SFPFM.GenerateLiveFingerprint(processingChunk, pickParam);

            if (rawFingerprints == null || rawFingerprints.Count == 0)
            {
                UpdatePickStatusMessage("🎤 오디오 수신 중... (핑거프린트 없음)");
                return;
            }

            // ★★★ 타임스탬프 보정 및 오버랩 영역 필터링 (TimeMs 기반 정밀 처리) ★★★
            var liveFingerprints = new List<FptEntry>();
            
            // ★★★ 2026-02-02: 타임스탬프 계산 방식 단순화 ★★★
            // 
            // 문제:
            //   - 오버랩 필터링 활성화 → 정답 해시 손실
            //   - 오버랩 필터링 비활성화 → 타임스탬프 음수
            // 
            // 새 접근법:
            //   - processingChunk는 4초 윈도우 (이전 3초 + 새 1초)
            //   - 윈도우의 시작 시간 = cumulativeMs - (processingChunk 길이)
            //   - hash.TimeMs는 윈도우 내 상대 시간 (0~4000ms)
            //   - absoluteTimeMs = 윈도우 시작 + hash.TimeMs
            //
            double processingDurationMs = processingChunk.Length * 1000.0 / sampleRate;
            int windowStartMs = Math.Max(0, cumulativeMs - (int)processingDurationMs);
            
            foreach (var entry in rawFingerprints)
            {
                if (entry.Hashes == null || entry.Hashes.Count == 0) continue;

                var validHashes = new List<FingerprintHashData>();
                foreach (var hash in entry.Hashes)
                {
                    // absoluteTimeMs = 윈도우 시작 시간 + 윈도우 내 상대 시간
                    int absoluteTimeMs = windowStartMs + hash.TimeMs;
                    
                    // 음수 방지 (초반 몇 초에서 발생 가능)
                    absoluteTimeMs = Math.Max(0, absoluteTimeMs);
                    
                    hash.TimeMs = absoluteTimeMs;
                    validHashes.Add(hash);
                }

                if (validHashes.Count > 0)
                {
                    entry.Hashes = validHashes;
                    // entry.Timestamp: 초 단위 (중앙값 사용)
                    int midIndex = validHashes.Count / 2;
                    entry.Timestamp = Math.Max(0, validHashes[midIndex].TimeMs / 1000);
                    liveFingerprints.Add(entry);
                }
            }


            int totalHashes = liveFingerprints.Sum(f => f.Hashes?.Count ?? 0);
            
            // ★★★ 특징 없는 구간 감지: 해시 수 기반 ★★★
            if (totalHashes < MinHashThreshold)
            {
                _consecutiveLowFeatureCount++;
                string lowFeatureMsg = $"⚠️ 저특징 구간 ({_consecutiveLowFeatureCount}초 연속, 해시: {totalHashes}개)";
                
                if (_consecutiveLowFeatureCount >= MaxConsecutiveLowFeature)
                {
                    // 10초 연속 저특징 → 알림 (매칭 시도는 계속)
                    lowFeatureMsg += " - 음원 시작 위치 변경 권장";
                    System.Diagnostics.Debug.WriteLine($"[SimTest] {lowFeatureMsg}");
                }
                
                UpdatePickStatusMessage(lowFeatureMsg);
                // 매칭은 계속 시도 (RealtimeFingerprintMatcher가 누적 판단)
            }
            else
            {
                _consecutiveLowFeatureCount = 0; // 정상 구간 → 카운트 리셋
            }
            
            // ★★★ 2026-02-07: 디버깅 - 원본 FPT vs 실시간 해시 비교 ★★★
            if (_movieRvsIndex != null && _expectTime.TotalSeconds > 0)
            {
                int expectedOffsetSec = (int)_expectTime.TotalSeconds;
                int rangeStart = Math.Max(0, expectedOffsetSec - 5);
                int rangeEnd = expectedOffsetSec + 5;
                
                // 실시간 생성된 해시 수집
                // ★★★ 2026-02-07: 버그 수정 - 16진수 문자열을 ulong으로 변환 ★★★
                var liveHashes = new HashSet<ulong>();
                foreach (var fp in liveFingerprints)
                {
                    if (fp.Hashes != null)
                    {
                        foreach (var h in fp.Hashes)
                        {
                            ulong hashVal = FingerprintHashData_mp.HexStringToUlong(h.Hash);
                            if (hashVal != 0UL)
                                liveHashes.Add(hashVal);
                        }
                    }
                }
                
                // 원본 FPT에서 expectedOffset 부근의 해시 찾기
                int originalHashesInRange = 0;
                int matchedHashCount = 0;
                var matchedOffsets = new Dictionary<int, int>(); // offset → count
                
                foreach (var fp in liveFingerprints)
                {
                    if (fp.Hashes == null) continue;
                    foreach (var h in fp.Hashes)
                    {
                        ulong hashVal = FingerprintHashData_mp.HexStringToUlong(h.Hash);
                        if (hashVal == 0UL) continue;
                        if (_movieRvsIndex.TryGetValue(hashVal, out var originalEntries))
                        {
                            foreach (var origTimeSec in originalEntries)
                            {
                                // ★★★ 2026-02-07: 버그 수정 - _movieRvsIndex의 값은 이미 초 단위! ★★★
                                // 원본 시간이 expectedOffset 부근인지 확인
                                if (origTimeSec >= rangeStart && origTimeSec <= rangeEnd)
                                {
                                    originalHashesInRange++;
                                    matchedHashCount++;
                                    
                                    // 오프셋 계산: 원본 시간 - 실시간 시간
                                    int liveSec = h.TimeMs / 1000;
                                    int offset = origTimeSec - liveSec;
                                    if (!matchedOffsets.ContainsKey(offset))
                                        matchedOffsets[offset] = 0;
                                    matchedOffsets[offset]++;
                                }
                            }
                        }
                    }
                }
                
                // 로그 출력
                System.Diagnostics.Debug.WriteLine($"\n★★★ [원본 FPT vs 실시간 해시 비교] ★★★");
                System.Diagnostics.Debug.WriteLine($"  기대 오프셋: {expectedOffsetSec}초 ({_expectTime})");
                System.Diagnostics.Debug.WriteLine($"  검색 범위: {rangeStart}~{rangeEnd}초");
                System.Diagnostics.Debug.WriteLine($"  실시간 해시 개수: {liveHashes.Count}개");
                System.Diagnostics.Debug.WriteLine($"  범위 내 매칭: {matchedHashCount}회 ({originalHashesInRange}개 원본 해시)");
                
                if (matchedOffsets.Count > 0)
                {
                    var topOffsets = matchedOffsets.OrderByDescending(kv => kv.Value).Take(5);
                    System.Diagnostics.Debug.WriteLine($"  매칭된 오프셋 분포:");
                    foreach (var kv in topOffsets)
                    {
                        System.Diagnostics.Debug.WriteLine($"    오프셋 {kv.Key}초: {kv.Value}회");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"  ⚠️ 범위 내 매칭된 해시 없음!");
                    
                    // 원본 FPT에 해당 범위의 해시가 있는지 확인
                    // ★★★ 2026-02-07: 버그 수정 - _movieRvsIndex의 값은 이미 초 단위! ★★★
                    int totalHashesInOriginalRange = 0;
                    var sampleOriginalHashes = new List<string>();
                    foreach (var kvp in _movieRvsIndex)
                    {
                        foreach (var origTimeSec in kvp.Value)
                        {
                            if (origTimeSec >= rangeStart && origTimeSec <= rangeEnd)
                            {
                                totalHashesInOriginalRange++;
                                if (sampleOriginalHashes.Count < 5)
                                {
                                    sampleOriginalHashes.Add($"0x{kvp.Key:X16}");
                                }
                            }
                        }
                    }
                    System.Diagnostics.Debug.WriteLine($"  원본 FPT의 {rangeStart}~{rangeEnd}초 범위 해시 개수: {totalHashesInOriginalRange}개");
                    
                    // ★★★ 2026-02-07: 해시 형식 비교 ★★★
                    System.Diagnostics.Debug.WriteLine($"\n  [해시 형식 비교]");
                    System.Diagnostics.Debug.WriteLine($"  실시간 해시 샘플 (처음 5개):");
                    int sampleCount = 0;
                    foreach (var hash in liveHashes.Take(5))
                    {
                        System.Diagnostics.Debug.WriteLine($"    [{sampleCount++}] 0x{hash:X16}");
                    }
                    System.Diagnostics.Debug.WriteLine($"  원본 FPT 해시 샘플 ({rangeStart}~{rangeEnd}초):");
                    for (int i = 0; i < sampleOriginalHashes.Count; i++)
                    {
                        System.Diagnostics.Debug.WriteLine($"    [{i}] {sampleOriginalHashes[i]}");
                    }
                    
                    // ★★★ 2026-02-07: Peak 정보 비교 (원본 FPT) ★★★
                    System.Diagnostics.Debug.WriteLine($"\n  [실시간 핑거프린트 Peak 샘플]");
                    foreach (var fp in liveFingerprints.Take(2))
                    {
                        System.Diagnostics.Debug.WriteLine($"  FptEntry Timestamp={fp.Timestamp}초:");
                        if (fp.Hashes != null)
                        {
                            foreach (var h in fp.Hashes.Take(3))
                            {
                                System.Diagnostics.Debug.WriteLine($"    Hash=0x{FingerprintHashData_mp.HexStringToUlong(h.Hash):X16}, F1={h.Frequency1:F0}Hz, F2={h.Frequency2:F0}Hz, dt={h.TimeDelta:F3}s");
                            }
                        }
                    }
                    
                    // ★★★ 2026-02-08: 원본 FPT Peak 정보 출력 ★★★
                    System.Diagnostics.Debug.WriteLine($"\n  [원본 FPT Peak 샘플 ({rangeStart}~{rangeEnd}초)]");
                    
                    // ★★★ 2026-02-08: 특정 해시의 역인덱스 존재 확인 ★★★
                    ulong testHash = 0x780019BFCE1CA4E4UL;
                    if (_movieRvsIndex.TryGetValue(testHash, out var testHashTimestamps))
                    {
                        var ts805Range = testHashTimestamps.Where(t => t >= 800 && t <= 810).ToList();
                        System.Diagnostics.Debug.WriteLine($"  [역인덱스 확인] 0x{testHash:X16}:");
                        System.Diagnostics.Debug.WriteLine($"    총 타임스탬프: {testHashTimestamps.Count}개");
                        System.Diagnostics.Debug.WriteLine($"    800~810초 범위: {ts805Range.Count}개 → {string.Join(", ", ts805Range.Take(5))}초");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"  [역인덱스 확인] 0x{testHash:X16}: ❌ 필터링됨 또는 존재하지 않음!");
                    }
                    
                    if (_movieFp != null)
                    {
                        var originalEntriesInRange = _movieFp.Where(fp => fp.Timestamp >= rangeStart && fp.Timestamp <= rangeEnd).Take(2).ToList();
                        foreach (var fp in originalEntriesInRange)
                        {
                            System.Diagnostics.Debug.WriteLine($"  FptEntry Timestamp={fp.Timestamp}초:");
                            if (fp.Hashes != null)
                            {
                                foreach (var h in fp.Hashes.Take(3))
                                {
                                    System.Diagnostics.Debug.WriteLine($"    Hash=0x{FingerprintHashData_mp.HexStringToUlong(h.Hash):X16}, F1={h.Frequency1:F0}Hz, F2={h.Frequency2:F0}Hz, dt={h.TimeDelta:F3}s");
                                }
                            }
                        }
                        if (originalEntriesInRange.Count == 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"  ⚠️ 원본 FPT에 {rangeStart}~{rangeEnd}초 범위 엔트리 없음!");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"  ⚠️ _movieFp가 null!");
                    }
                }
                System.Diagnostics.Debug.WriteLine($"★★★ [비교 끝] ★★★\n");
            }
            
            // ★★★ 2026-02-07: Coarse-to-Fine 매칭 적용 ★★★
            // 1단계 (Coarse): 10초 region별 매칭 점수 → 상위 5개 후보 추출
            // 2단계 (Fine): 후보 region에서만 SFPFM.MatchFingerprints() 정밀 매칭
            var coarseToFineResult = MultiResolutionMatching.MatchCoarseToFineRealtime(
                liveFingerprints,
                _movieRvsIndex,
                regionSize: 5,       // ★★★ 2026-02-08: 5초 단위 region (10→5초, 해상도 증가) ★★★
                topCandidates: 10,   // ★★★ 2026-02-08: 상위 10개 region Fine 매칭 (5→10 확장) ★★★
                minConfidence: 0.20);
            
            // ★ 오프셋 집중도 계산 (진단/로깅용) ★
            var (calcResult, offsetConcentration) = SFPFM.CalcOffsetConcentration(
                liveFingerprints, _movieRvsIndex, maxHashOccurrences: SFPFM.DefaultMaxHashOccurrences);
            
            // ★★★ 불확실 구간 감지: 집중도 기반 (로그만, 건너뛰지 않음) ★★★
            if (offsetConcentration < MinConcentrationLog && totalHashes >= MinHashThreshold)
            {
                System.Diagnostics.Debug.WriteLine($"[SimTest] 🔍 저집중도 구간: C={offsetConcentration:P1}, 해시={totalHashes}개 @ {_cumulativeRecordingSeconds}초");
            }
            
            // ★★★ 2026-02-07: Coarse-to-Fine 결과 기반 Early Exit ★★★
            // ★★★ 수정: 전체 집중도(offsetConcentration)도 함께 확인해야 함! ★★★
            // 문제: coarseToFineResult.Confidence는 region 내 신뢰도일 뿐, 전체 집중도와 다름
            // 해결: 전체 집중도가 충분히 높을 때만 Early Exit 허용
            const double EarlyExitConfidenceThreshold = 0.50;       // region 내 신뢰도 50% 이상
            const double EarlyExitConcentrationThreshold = 0.50;    // ★ 전체 집중도 50% 이상 (35%→50% 상향) ★
            
            bool isHighConfidence = coarseToFineResult.IsMatched && coarseToFineResult.Confidence >= EarlyExitConfidenceThreshold;
            bool isHighConcentration = offsetConcentration >= EarlyExitConcentrationThreshold;
            
            if (isHighConfidence && isHighConcentration)
            {
                System.Diagnostics.Debug.WriteLine($"[SimTest] ⚡ Coarse-to-Fine Early Exit: {coarseToFineResult.MatchedTime.TotalSeconds:F1}초 (RegionC={coarseToFineResult.Confidence:P1}, GlobalC={offsetConcentration:P1})");
                UpdatePickStatusMessage($"⚡ 빠른 매칭! (집중도: {offsetConcentration:P0})");
                
                // RealtimeMatchResult로 변환하여 안정 매칭 처리
                var stableResult = new RealtimeMatchResult
                {
                    IsMatched = true,
                    IsStableMatch = true,
                    Confidence = offsetConcentration, // ★ 전체 집중도 사용 ★
                    MatchedOriginalTime = coarseToFineResult.MatchedTime,
                    ConsecutiveMatchCount = 3 // 안정 매칭으로 간주
                };
                OnStableMatchFound(stableResult);
                return;
            }
            else if (isHighConfidence && !isHighConcentration)
            {
                // ★ 집중도가 낮으면 Early Exit하지 않고 연속 매칭 검증으로 넘김 ★
                System.Diagnostics.Debug.WriteLine($"[SimTest] ⚠️ Coarse-to-Fine 매칭됨 but 집중도 낮음: {coarseToFineResult.MatchedTime.TotalSeconds:F1}초 (RegionC={coarseToFineResult.Confidence:P1}, GlobalC={offsetConcentration:P1}) → 연속 매칭 검증 필요");
            }
            
            // ★★★ 시각적 진행률 표시 ★★★
            string state = coarseToFineResult.IsMatched ? "matching" : 
                           totalHashes < MinHashThreshold ? "weak" : "searching";
            double displayConcentration = coarseToFineResult.IsMatched ? coarseToFineResult.Confidence : offsetConcentration;
            UpdateMatchingProgress(_cumulativeRecordingSeconds, displayConcentration, totalHashes, state);

            // ★★★ 2026-02-07: Coarse-to-Fine 결과를 RealtimeFingerprintMatcher에 전달 ★★★
            // Coarse-to-Fine에서 매칭이 성공하면 _matcher에 결과 추가하여 연속 매칭 검증
            if (coarseToFineResult.IsMatched && _matcher != null)
            {
                // 매칭된 핑거프린트를 _matcher에 추가 (연속 매칭 카운트 업데이트)
                foreach (var entry in liveFingerprints)
                {
                    var result = _matcher.AddFingerprint(entry);
                    
                    // 매칭 결과 처리 (연속 3회 확인)
                    if (result.IsStableMatch)
                    {
                        // 안정적인 매칭 (3회 연속 + 오프셋 안정성)
                        System.Diagnostics.Debug.WriteLine($"[SimTest] ✅ 안정 매칭! (Coarse-to-Fine + 연속 {result.ConsecutiveMatchCount}회)");
                        OnStableMatchFound(result);
                        return;
                    }
                    else if (result.IsMatched)
                    {
                        // 단발성 매칭 (연속 카운트 증가 중)
                        OnPossibleMatchFound(result);
                    }
                }
            }
            else
            {
                // Coarse-to-Fine 매칭 실패 시에도 _matcher로 폴백 (기존 로직)
                foreach (var entry in liveFingerprints)
                {
                    var result = _matcher.AddFingerprint(entry);
                    
                    if (result.IsStableMatch)
                    {
                        OnStableMatchFound(result);
                    }
                    else if (result.IsMatched)
                    {
                        OnPossibleMatchFound(result);
                    }
                }
            }
        }
        
        /// <summary>
        /// AudioMatchingService에서 안정적인 매칭이 발생했을 때 호출되는 콜백
        /// </summary>
        private void MatchingService_StableMatchFound(object sender, MatchEventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new Action<object, MatchEventArgs>(MatchingService_StableMatchFound), sender, e);
                return;
            }

            try
            {
                // 찾은 시가을 txtTimeFound에 표시
                if (txtTimeFound != null)
                {
                    var t = e.MatchedTime;
                    txtTimeFound.Text = $"{t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
                }

                // 상태 메시지 업데이트
                    string timeStr = $"{e.MatchedTime.Hours:D2}:{e.MatchedTime.Minutes:D2}:{e.MatchedTime.Seconds:D2}";
                UpdatePickStatusMessage($"✓ 매칭 완료! {timeStr} (신뢰도: {e.Confidence:P0}) → 재생 이동");

                // 영화 오디오가 로드되어 있다면 해당 위치로 점프
                if (_audioFileReader != null && _totalDuration.TotalSeconds > 0 && trackBarPlayProgress != null)
                {
                    // 재생 위치 이동
                    _audioFileReader.CurrentTime = e.MatchedTime;

                    // 트랙바 및 진행 표시 업데이트
                    double progress = e.MatchedTime.TotalSeconds / _totalDuration.TotalSeconds;
                    progress = Math.Max(0.0, Math.Min(1.0, progress));

                    int trackBarValue = (int)(progress * 1000);
                    trackBarPlayProgress.Value = Math.Max(0, Math.Min(1000, trackBarValue));

                    UpdatePlayTimeDisplay(e.MatchedTime);
                    pnlPlayProgress?.Invalidate();

                    // 자동 재생 시작
                    if (_waveOut != null && _waveOut.PlaybackState != PlaybackState.Playing)
                    {
                        _waveOut.Play();
                    }
                }
            }
            catch
            {
                // UI 업데이트 중 발생하는 예외는 무시 (로그만 남길 수 있음)
            }
            finally
            {
                // 안정 매칭이 한 번 발생하면 자동으로 실시간 매칭 종료
                if (_isMatching)
                {
                    try
                    {
                        StopMatching();
                    }
                    catch { }

                    StableMatchFound -= MatchingService_StableMatchFound;
                    _isMatching = false;

                    if (btnLivePickSnipet != null)
                    {
                        btnLivePickSnipet.Text = "Live Pick";
                        btnLivePickSnipet.BackColor = SystemColors.ButtonFace;
                    }
                }
            }
        }
        /// <summary>
        /// 안정적인 매칭 발견 시 콜백
        /// </summary>
        private void OnStableMatchFound(RealtimeMatchResult result)
        {
            Console.WriteLine($"[Live 시도] 상영 시간: {_expectTime}, " + $"시간차: {result.MatchedOriginalTime - _expectTime}");
            // UI 업데이트 또는 이벤트 발생
            Console.WriteLine($"[안정 매칭] 원본 시간: {result.MatchedOriginalTime}, " +
                              $"신뢰도: {result.Confidence:P1}, " +
                              $"연속 매칭: {result.ConsecutiveMatchCount}회");

            // 상태 메시지 업데이트
            string timeStr = $"{result.MatchedOriginalTime.Hours:D2}:{result.MatchedOriginalTime.Minutes:D2}:{result.MatchedOriginalTime.Seconds:D2}";
            UpdatePickStatusMessage($"✓ 안정 매칭! {timeStr} (신뢰도: {result.Confidence:P0}, 연속: {result.ConsecutiveMatchCount}회)");

            // 이벤트 발생
            StableMatchFound?.Invoke(this, new MatchEventArgs
            {
                MatchedTime = result.MatchedOriginalTime,
                Confidence = result.Confidence
            });
        }

        /// <summary>
        /// 가능한 매칭 발견 시 콜백
        /// </summary>
        private void OnPossibleMatchFound(RealtimeMatchResult result)
        {
            Console.WriteLine($"[가능한 매칭] 원본 시간: {result.MatchedOriginalTime}, " + $"신뢰도: {result.Confidence:P1}");

            // 상태 메시지 업데이트
            string timeStr = $"{result.MatchedOriginalTime.Hours:D2}:{result.MatchedOriginalTime.Minutes:D2}:{result.MatchedOriginalTime.Seconds:D2}";
            UpdatePickStatusMessage($"🔍 가능한 매칭: {timeStr} (신뢰도: {result.Confidence:P0})");
        }

        /// <summary>
        /// 핑거프린트 매칭을 중단합니다.
        /// </summary>
        private void StopMatching()
        {
            if (_matchingTimer != null)
            {
                _matchingTimer.Stop();
                _matchingTimer.Dispose();
                _matchingTimer = null;
            }

            if (_fingerprintMatchingCts != null)
            {
                _fingerprintMatchingCts.Cancel();
                _fingerprintMatchingCts.Dispose();
                _fingerprintMatchingCts = null;
            }

            if (_waveIn != null)
            {
                // 오디오 캡처 중단
                try { _waveIn.StopRecording(); } catch { }
                try { _waveIn.Dispose(); } catch { }
                _waveIn = null;
            }
            
            // ★ 디버그 Writer 정리 (파일 Flush 및 닫기 보장) ★
            if (_debugWriter != null)
            {
                try { _debugWriter.Dispose(); } catch { }
                _debugWriter = null;
                System.Diagnostics.Debug.WriteLine("[StopMatching] Debug WAV file closed.");
            }

            // 상태값 초기화
            _isMatching = false;

            lock (_audioBufferLock)
            {
                _audioBuffer.Clear();
            }
            lock (_matchesLock)
            {
                _consecutiveMatches = 0;
            }
        }

        /// <summary>
        /// ★★★ Live vs 원본 핑거프린트 생성 방식 비교 진단 ★★★
        /// 영화 파일에서 직접 오디오를 추출하여 GenerateLiveFingerprint로 테스트합니다.
        /// 키보드 단축키: Ctrl+Shift+D 또는 코드에서 직접 호출
        /// </summary>
        public async Task RunLiveFingerprintDiagnostic()
        {
            if (_movieRvsIndex == null || _movieRvsIndex.Count == 0)
            {
                MessageBox.Show("먼저 영화 핑거프린트를 로드해주세요.", "진단 불가", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 영화 파일 경로 선택
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "진단할 영화 파일 선택";
                ofd.Filter = "동영상 파일|*.mp4;*.mkv;*.avi;*.mov;*.wav;*.mp3|모든 파일|*.*";
                
                if (ofd.ShowDialog() != DialogResult.OK)
                    return;

                string movieFilePath = ofd.FileName;

                // 시작 시간 입력 (간단한 InputBox 대체)
                int startTimeSec = 60; // 기본값: 1분
                using (var inputForm = new Form())
                {
                    inputForm.Text = "시작 시간 입력";
                    inputForm.Size = new Size(350, 150);
                    inputForm.FormBorderStyle = FormBorderStyle.FixedDialog;
                    inputForm.StartPosition = FormStartPosition.CenterParent;
                    inputForm.MaximizeBox = false;
                    inputForm.MinimizeBox = false;

                    var label = new Label { Text = "진단할 시작 시간 (초):", Left = 20, Top = 20, Width = 150 };
                    var textBox = new TextBox { Text = "60", Left = 170, Top = 18, Width = 120 };
                    var btnOK = new Button { Text = "확인", DialogResult = DialogResult.OK, Left = 150, Top = 60, Width = 80 };
                    
                    inputForm.Controls.Add(label);
                    inputForm.Controls.Add(textBox);
                    inputForm.Controls.Add(btnOK);
                    inputForm.AcceptButton = btnOK;

                    if (inputForm.ShowDialog(this) != DialogResult.OK)
                        return;

                    string inputTime = textBox.Text.Trim();
                    if (inputTime.Contains(":"))
                    {
                        // HH:MM:SS 형식
                        if (TimeSpan.TryParse(inputTime, out TimeSpan ts))
                            startTimeSec = (int)ts.TotalSeconds;
                        else
                        {
                            MessageBox.Show("시간 형식이 올바르지 않습니다.", "입력 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                    }
                    else
                    {
                        if (!int.TryParse(inputTime, out startTimeSec))
                        {
                            MessageBox.Show("숫자를 입력해주세요.", "입력 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                    }
                }

                // 진단 실행
                UpdatePickStatusMessage("🔍 Live 핑거프린트 진단 중...");

                // ★★★ 2026-02-03: 원본 FPT와 동일한 설정 적용 ★★★
                // 문제: StartMatching()에서 설정한 값이 여기서는 적용되지 않음
                // 해결: 진단 함수에서도 동일한 설정 적용
                pa.workTab.pickParam.UseQualityBasedFiltering = false;
                pa.workTab.pickParam.QualityThreshold = 0.0;
                pa.workTab.pickParam.minMagnitude = 0.0;

                // ★★★ 2026-02-03: FingerprintConfig 출력 ★★★
                var cfg = pa.workTab.pickParam.fptCfg;
                System.Diagnostics.Debug.WriteLine($"\n★★★ [FingerprintConfig 진단] ★★★");
                System.Diagnostics.Debug.WriteLine($"  FFTSize: {cfg.FFTSize}");
                System.Diagnostics.Debug.WriteLine($"  HopSize: {cfg.HopSize}");
                System.Diagnostics.Debug.WriteLine($"  MaxPeaksPerFrame: {cfg.MaxPeaksPerFrame}");
                System.Diagnostics.Debug.WriteLine($"  PeakNeighborhoodSize: {cfg.PeakNeighborhoodSize}");
                System.Diagnostics.Debug.WriteLine($"  PeakThresholdMultiplier: {cfg.PeakThresholdMultiplier}");
                System.Diagnostics.Debug.WriteLine($"  SampleRate: {pa.workTab.pickParam.sampleRate}");
                System.Diagnostics.Debug.WriteLine($"★★★ [FingerprintConfig 진단 끝] ★★★\n");
                
                // ★★★ 2026-02-03: 원본 FPT의 해당 타임스탬프 해시 직접 출력 ★★★
                System.Diagnostics.Debug.WriteLine($"\n★★★ [원본 FPT 해시 진단] ★★★");
                if (_movieFp != null)
                {
                    // 해당 타임스탬프 (±1초) 범위의 엔트리 찾기
                    var matchingEntries = _movieFp.Where(e => e.Timestamp >= startTimeSec - 1 && e.Timestamp <= startTimeSec + 5).ToList();
                    System.Diagnostics.Debug.WriteLine($"  원본 FPT에서 {startTimeSec}~{startTimeSec+5}초 범위 엔트리: {matchingEntries.Count}개");
                    
                    if (matchingEntries.Count > 0)
                    {
                        int hashCount = 0;
                        foreach (var entry in matchingEntries.Take(3))
                        {
                            System.Diagnostics.Debug.WriteLine($"  [Timestamp={entry.Timestamp}초] 해시 {entry.Hashes?.Count ?? 0}개");
                            if (entry.Hashes != null)
                            {
                                foreach (var hash in entry.Hashes.Take(5))
                                {
                                    System.Diagnostics.Debug.WriteLine($"    '{hash.Hash}' (F1={hash.Frequency1:F0}, F2={hash.Frequency2:F0}, dt={hash.TimeDelta:F3})");
                                    hashCount++;
                                }
                                if (entry.Hashes.Count > 5)
                                    System.Diagnostics.Debug.WriteLine($"    ... (총 {entry.Hashes.Count}개)");
                            }
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"  ⚠️ 해당 범위의 엔트리가 없습니다!");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"  ⚠️ _movieFp가 null입니다!");
                }
                System.Diagnostics.Debug.WriteLine($"★★★ [원본 FPT 해시 진단 끝] ★★★\n");

                var result = await Task.Run(() =>
                {
                    return SFPFM.DiagnoseLiveFingerprintGeneration(
                        movieFilePath,
                        startTimeSec,
                        durationSec: 5, // 5초 구간 진단
                        _movieRvsIndex,
                        pa.workTab.pickParam,
                        _movieFp,  // 원본 FPT 리스트 전달 (해시 비교용)
                        msg => System.Diagnostics.Debug.WriteLine(msg));
                });

                if (result.success && result.matchResult != null)
                {
                    string msg = $"진단 완료!\n\n" +
                                 $"예상 시간: {TimeSpan.FromSeconds(startTimeSec):hh\\:mm\\:ss}\n" +
                                 $"매칭 시간: {result.matchResult.MatchedTime:hh\\:mm\\:ss}\n" +
                                 $"시간차: {result.matchResult.MatchedTime - TimeSpan.FromSeconds(startTimeSec):hh\\:mm\\:ss}\n" +
                                 $"신뢰도: {result.matchResult.Confidence:P1}\n" +
                                 $"집중도: {result.concentration:P2}\n\n" +
                                 $"자세한 결과는 디버그 출력 창을 확인하세요.";
                    
                    MessageBox.Show(msg, "Live 핑거프린트 진단 결과", MessageBoxButtons.OK, 
                        result.matchResult.IsMatched ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    
                    UpdatePickStatusMessage($"✓ 진단 완료 (집중도: {result.concentration:P2})");
                }
                else
                {
                    MessageBox.Show("진단 실패. 디버그 출력 창을 확인하세요.", "진단 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    UpdatePickStatusMessage("❌ 진단 실패");
                }
            }
        }

        /// <summary>
        /// dgvPickedFeatures에 항목 추가
        /// </summary>
        private void AddPickedFeatureToGrid(string featureFilePath, TimeSpan startTime, int termMs)
        {
            if (dgvPickedFpts == null)
            {
                return;
            }

            // UI 스레드에서만 실행되도록 보장
            if (InvokeRequired)
            {
                Invoke(new Action<string, TimeSpan, int>(AddPickedFeatureToGrid), featureFilePath, startTime, termMs);
                return;
            }

            // 폼이 dispose되었거나 dispose 중이면 무시
            if (IsDisposed || Disposing)
            {
                return;
            }

            try
            {
                // 행 추가
                int rowIndex = dgvPickedFpts.Rows.Add();
                var row = dgvPickedFpts.Rows[rowIndex];

                // # 컬럼 (행 번호)
                if (dgvPickedFpts.Columns.Contains("colNumber"))
                {
                    row.Cells["colNumber"].Value = (rowIndex + 1).ToString();
                }

                // Picked 컬럼 (파일명)
                if (dgvPickedFpts.Columns.Contains("colPicked"))
                {
                    row.Cells["colPicked"].Value = Path.GetFileName(featureFilePath);
                }

                // sr/mt/dt/verdict 컬럼 초기값
                if (dgvPickedFpts.Columns.Contains("colSr"))
                {
                    row.Cells["colSr"].Value = "-";
                }
                if (dgvPickedFpts.Columns.Contains("colMt"))
                {
                    row.Cells["colMt"].Value = "-";
                }
                if (dgvPickedFpts.Columns.Contains("colDt"))
                {
                    row.Cells["colDt"].Value = "-";
                }
                if (dgvPickedFpts.Columns.Contains("colVerdict"))
                {
                    row.Cells["colVerdict"].Value = "-";
                }

                // FS+# 컬럼들 (각 체크셋별 유사도 - 나중에 계산)
                // FS#F (찾은 시간), FS#S (유사도), FS#D (소요 시간), FS#V (판정) 초기값 설정
                for (int i = 1; i <= _checksetHandler.checkSet.items.Count; i++)
                {
                    string colNameF = $"colFS{i}F";
                    string colNameS = $"colFS{i}S";
                    string colNameD = $"colFS{i}D";
                    string colNameV = $"colFS{i}V";
                    
                    if (dgvPickedFpts.Columns.Contains(colNameF))
                    {
                        row.Cells[colNameF].Value = "-"; // 찾은 시간 초기값
                    }
                    if (dgvPickedFpts.Columns.Contains(colNameS))
                    {
                        row.Cells[colNameS].Value = "-"; // 유사도 초기값
                    }
                    if (dgvPickedFpts.Columns.Contains(colNameD))
                    {
                        row.Cells[colNameD].Value = "-"; // 소요 시간 초기값
                    }
                    if (dgvPickedFpts.Columns.Contains(colNameV))
                    {
                        row.Cells[colNameV].Value = "-"; // 판정 초기값
                    }
                }

                // Tag에 파일 경로 저장
                row.Tag = featureFilePath;
            }
            catch (ObjectDisposedException)
            {
                // 폼이나 컨트롤이 dispose된 경우 무시
            }
            catch (InvalidOperationException)
            {
                // 컨트롤이 유효하지 않은 경우 무시
            }
            catch (Exception ex)
            {
                // 기타 예외는 로그만 남기고 무시
                System.Diagnostics.Debug.WriteLine($"AddPickedFeatureToGrid 오류: {ex.Message}");
            }
        }

        private void BtnNoiseFilterSettings_Click(object sender, EventArgs e)
        {
            var noiseFilterService = new Audio.Processing.NoiseFilterService();
            using (var dialog = new NoiseFilterSettingsForm(noiseFilterService))
            {
                dialog.ShowDialog(this);
            }
        }
        private string getDgvFptsColumnText(int rowIndex, string colName)
        {
            if (dgvPickedFpts.Columns.Contains(colName))
            {
                var row = dgvPickedFpts.Rows[rowIndex];
                if (row == null) return null;
                
                var cell = row.Cells[colName];
                if (cell != null && cell.Value != null)
                {
                    return cell.Value.ToString();
                }
            }
            return null;
        }
        /// <summary>
        /// dgvPickedFeatures CellDoubleClick 이벤트 핸들러
        /// pickedFp와 movieFp를 매칭하여 matchedTime을 찾고 영화 오디오를 재생합니다.
        /// </summary>
        private void DgvPickedFeatures_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || dgvPickedFpts == null)
            {
                return;
            }
            ProcessSelectedFingerprint(e.RowIndex);
        }

        /// <summary>
        /// 선택된 핑거프린트를 처리합니다. (DoubleClick 및 Enter 키 공통 로직)
        /// </summary>
        private async void ProcessSelectedFingerprint(int rowIndex)
        {
            if (rowIndex < 0 || dgvPickedFpts == null || rowIndex >= dgvPickedFpts.Rows.Count)
            {
                return;
            }
            var row = dgvPickedFpts.Rows[rowIndex];
            string fileName = getDgvFptsColumnText(rowIndex, "colPicked");
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            // _movieFp가 로드되어 있는지 확인, 로딩 중인 경우 대기
            if (_isFingerprintLoading)
            {
                UpdateMatchStatusMessage("영화 핑거프린트 로딩 중...");
                MessageBox.Show("영화 핑거프린트를 로드하는 중입니다. 잠시 후 다시 시도해주세요.", "로딩 중", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            
            // 파일 경로 확인
            var fpFilePath = _fptFile.GetFeatureFilePath();
            bool fileExists = File.Exists(fpFilePath);
            
            try
            {
                // 진행 상태 표시: 시작
                UpdateMatchStatusMessage($"매칭 시작: {fileName}");
                
                // pickedFp 파일 로드
                string pickedFpPath = null;
                if (!string.IsNullOrWhiteSpace(_fptFile.featureDir) && Directory.Exists(_fptFile.featureDir))
                {
                    pickedFpPath = Path.Combine(_fptFile.featureDir, fileName);
                }

                if (string.IsNullOrWhiteSpace(pickedFpPath) || !File.Exists(pickedFpPath))
                {
                    UpdateMatchStatusMessage("오류: 파일을 찾을 수 없음");
                    MessageBox.Show($"핑거프린트 파일을 찾을 수 없습니다.\n{fileName}", "파일 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // pickedFp 로드
                UpdateMatchStatusMessage("핑거프린트 파일 로드 중...");
                List<FptEntry> pickedFp = null;
                try
                {
                    pickedFp = await Task.Run(() => SFPFM.LoadFingerprintsFromFile(pickedFpPath));
                }
                catch (Exception loadEx)
                {
                    // 로드 과정에서 예외가 발생한 경우: 상세 정보 표시
                    UpdateMatchStatusMessage($"오류: 파일 로드 실패 - {loadEx.Message}");
                    MessageBox.Show(
                        "핑거프린트 파일을 로드하는 중 오류가 발생했습니다.\n\n" + $"파일 경로: {pickedFpPath}\n" + $"오류: {loadEx.Message}",
                        "로드 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (pickedFp == null || pickedFp.Count == 0)
                {
                    // 파일은 읽었지만 FingerprintEntry가 없을 때
                    UpdateMatchStatusMessage("오류: 핑거프린트가 비어있음");
                    MessageBox.Show(
                        "핑거프린트 파일을 로드할 수 없습니다.\n\n" + $"파일 경로: {pickedFpPath}\n" +
                        "파일이 손상되었거나 지원되지 않는 형식일 수 있습니다.\n" + "필요하다면 해당 핑거프린트를 다시 생성해 주세요.",
                        "로드 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // dgvPickedFeatures의 verdict 컬럼 리셋
                if (dgvPickedFpts.Columns.Contains("colVerdict"))
                {
                    row.Cells["colVerdict"].Value = "-"; 
                }

                // 파일명에서 pickTime 추출 (매칭 결과 검증용)
                TimeSpan? expectedPickTime = null;
                try
                {
                    // 파일명 형식: movieID_pickTimeHMS_termMs.fp.mpack
                    // 예: "2_011533.090_200.fp.mpack"
                    string nameWithoutExt1 = Path.GetFileNameWithoutExtension(fileName);   // *.fp
                    string nameWithoutExt2 = Path.GetFileNameWithoutExtension(nameWithoutExt1);  // movieID_pickTimeHMS_termMs
                    string[] parts = nameWithoutExt2.Split('_');
                    if (parts.Length >= 2)
                    {
                        string timeMs = string.Copy(parts[1]);
                        if (ConvertToTimeSpan(timeMs, out TimeSpan pickTime))
                        {
                            expectedPickTime = pickTime;
                            System.Diagnostics.Debug.WriteLine($"DgvPickedFeatures_CellDoubleClick: 파일명에서 추출한 pickTime = {pickTime:hh\\:mm\\:ss\\.fff}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"DgvPickedFeatures_CellDoubleClick: pickTime 추출 실패 - {ex.Message}");
                }

                // movieFp와 매칭하여 matchedTime 찾기 (소요 시간 측정 포함)
                // 역인덱스가 있으면 우선 사용 (매칭 성능 향상)
                UpdateMatchStatusMessage("영화 핑거프린트와 매칭 중...");
                var matchStopwatch = System.Diagnostics.Stopwatch.StartNew();

                var matchResult = await Task.Run(() =>
                    SFPFM.MatchFingerprints(pickedFp, _movieFp, _movieRvsIndex, minConfidence: 0.5, maxHashOccurrences: SFPFM.DefaultMaxHashOccurrences));

                matchStopwatch.Stop();

                // 매칭 결과 진단
                if (matchResult == null || !matchResult.IsMatched)
                {
                    // 매칭 실패 시
                    string confidenceStr = matchResult != null ? matchResult.Confidence.ToString("P0") : "N/A";
                    UpdateMatchStatusMessage($"매칭 실패 (신뢰도: {confidenceStr}) - 진단 중...");
                    MatchDiagnostics diagResult = MatchDiagnostics.Analyze(pickedFp, _movieRvsIndex);
                    _lastOffsetConcentration = diagResult.OffsetConcentration;
                    Bases.WriteDiagToFile(pickedFpPath, diagResult, false);
                    // ★ 필터링 효과 비교 추가 ★
                    //SFPFM.CompareFilteringEffect(pickedFp, _movieRvsIndex);

                    // verdict: 판정 (실패)
                    if (dgvPickedFpts.Columns.Contains("colVerdict"))
                    {
                        row.Cells["colVerdict"].Value = "Fail" + $", 신뢰도: {confidenceStr}";
                    }
                    UpdateMatchStatusMessage($"✗ 매칭 실패 (신뢰도: {confidenceStr}, 소요: {matchStopwatch.ElapsedMilliseconds}ms)");
                    return;
                } else {
                    UpdateMatchStatusMessage($"매칭 성공! 진단 정보 저장 중...");
                    MatchDiagnostics diagResult = MatchDiagnostics.Analyze(pickedFp, _movieRvsIndex);
                    _lastOffsetConcentration = diagResult.OffsetConcentration;
                    Bases.WriteDiagToFile(pickedFpPath, diagResult, true);
                    // ★ 필터링 효과 비교 추가 ★
                    //SFPFM.CompareFilteringEffect(pickedFp, _movieRvsIndex);
                }

                // matchedTime 가져오기 (hash 값만으로 찾은 매칭 시간)
                TimeSpan matchedTime = matchResult.MatchedTime;

                // 파일명의 pickTime은 검증용으로만 사용 (보정하지 않음)
                if (expectedPickTime.HasValue)
                {
                    // pickedFp의 첫 번째 타임스탬프 확인
                    int pickedFpFirstTimestamp = pickedFp != null && pickedFp.Count > 0 ? pickedFp[0].Timestamp : 0;
                    int matchedTimestamp = (int)matchedTime.TotalSeconds;
                    double expectedTimestamp = expectedPickTime.Value.TotalSeconds;
                    
                    System.Diagnostics.Debug.WriteLine($"DgvPickedFeatures_CellDoubleClick: 매칭 결과 검증");
                    System.Diagnostics.Debug.WriteLine($"  예상 시간 (pickTime, 검증용): {expectedPickTime.Value:hh\\:mm\\:ss\\.fff} ({expectedTimestamp:F3}초)");
                    System.Diagnostics.Debug.WriteLine($"  매칭 시간 (matchedTime, hash 기반): {matchedTime:hh\\:mm\\:ss\\.fff} ({matchedTimestamp}초)");
                    System.Diagnostics.Debug.WriteLine($"  pickedFp 첫 번째 타임스탬프: {pickedFpFirstTimestamp}초");
                    
                    double timeDifference = matchedTime.TotalSeconds - expectedPickTime.Value.TotalSeconds;
                    System.Diagnostics.Debug.WriteLine($"  시간 차이: {timeDifference:F3}초");
                    
                    // 검증: 시간 차이가 크면(1초 이상) 경고만 출력 (보정하지 않음)
                    if (Math.Abs(timeDifference) > 1.0)
                    {
                        System.Diagnostics.Debug.WriteLine($"  ⚠️ 경고: 매칭 시간이 예상 시간과 {Math.Abs(timeDifference):F3}초 차이남!");
                        System.Diagnostics.Debug.WriteLine($"  매칭 결과는 hash 값만으로 찾은 시간을 그대로 사용합니다.");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"  ✓ 매칭 시간이 예상 시간과 일치합니다.");
                    }
                }
                else
                {
                    // 파일명에서 pickTime을 추출할 수 없는 경우
                    System.Diagnostics.Debug.WriteLine($"DgvPickedFeatures_CellDoubleClick: 파일명에서 pickTime을 추출할 수 없습니다.");
                    System.Diagnostics.Debug.WriteLine($"  파일명: {fileName}");
                    System.Diagnostics.Debug.WriteLine($"  매칭 결과 (hash 기반): {matchedTime:hh\\:mm\\:ss\\.fff}");
                    System.Diagnostics.Debug.WriteLine($"  ⚠️ 주의: 파일명 형식이 예상과 다를 수 있습니다. (예상 형식: movieID_pickTimeHMS_termMs.fp.mpack)");
                }

                // dgvPickedFeatures에 sr/mt/dt 값 기록
                if (dgvPickedFpts != null)
                {
                    // sr: 유사도 (Confidence)
                    if (dgvPickedFpts.Columns.Contains("colSr"))
                    {
                        row.Cells["colSr"].Value = matchResult.Confidence.ToString("F3");
                    }

                    // mt: 매칭 시간 (MatchedTime)
                    if (dgvPickedFpts.Columns.Contains("colMt"))
                    {
                        row.Cells["colMt"].Value = FormatTimeSpan(matchedTime);
                    }

                    // dt: 소요 시간 (매칭 수행 시간)
                    if (dgvPickedFpts.Columns.Contains("colDt"))
                    {
                        int totalMs = (int)matchStopwatch.ElapsedMilliseconds;
                        int seconds = totalMs / 1000;
                        int milliseconds = totalMs % 1000;
                        row.Cells["colDt"].Value = $"{seconds}초{milliseconds}ms";
                    }

                    // verdict: 판정 (성공)
                    if (dgvPickedFpts.Columns.Contains("colVerdict"))
                    {
                        row.Cells["colVerdict"].Value = "OK" + $", 신뢰도: {matchResult.Confidence:P0}";
                    }
                }

                // txtTimePicked에 시간값 설정
                if (txtTimePlay != null)
                {
                    txtTimePlay.Text = $"{matchedTime.Hours:D2}:{matchedTime.Minutes:D2}:{matchedTime.Seconds:D2}.{matchedTime.Milliseconds:D3}";
                }

                // 영화 오디오 재생 위치를 matchedTime으로 설정
                if (_audioFileReader != null && _totalDuration.TotalSeconds > 0 && trackBarPlayProgress != null)
                {
                    double progress = matchedTime.TotalSeconds / _totalDuration.TotalSeconds;
                    progress = Math.Max(0.0, Math.Min(1.0, progress)); // 0~1 범위로 제한
                    
                    // 오디오 재생 위치 변경
                    _audioFileReader.CurrentTime = matchedTime;
                    
                    // 트랙바 위치 업데이트
                    int trackBarValue = (int)(progress * 1000);
                    trackBarPlayProgress.Value = Math.Max(0, Math.Min(1000, trackBarValue));
                    
                    // 재생 시간 표시 업데이트
                    UpdatePlayTimeDisplay(matchedTime);
                    
                    // 재생 시작 (재생 중이 아니면)
                    if (_waveOut != null && _waveOut.PlaybackState != PlaybackState.Playing)
                    {
                        _waveOut.Play();
                    }
                    
                    // Panel을 다시 그려서 세로선 업데이트
                    pnlPlayProgress?.Invalidate();
                }

                // 최종 상태 메시지
                UpdateMatchStatusMessage($"✓ 매칭 완료: {FormatTimeSpan(matchedTime)} (신뢰도: {matchResult.Confidence:P0}, 소요: {matchStopwatch.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                UpdateMatchStatusMessage($"오류: {ex.Message}");
                MessageBox.Show($"매칭 중 오류가 발생했습니다.\n{ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 매칭 상태 메시지를 lbl_matchStatusMsg에 표시합니다.
        /// </summary>
        private void UpdateMatchStatusMessage(string message)
        {
            if (lbl_matchStatusMsg == null)
            {
                return;
            }

            if (InvokeRequired)
            {
                Invoke(new Action<string>(UpdateMatchStatusMessage), message);
                return;
            }

            try
            {
                lbl_matchStatusMsg.Text = message;
                lbl_matchStatusMsg.Refresh(); // 즉시 UI 갱신
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateMatchStatusMessage 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// Pick 상태 메시지를 lbl_pickStatusMsg에 표시합니다.
        /// </summary>
        private void UpdatePickStatusMessage(string message)
        {
            if (lbl_pickStatusMsg == null)
            {
                return;
            }

            if (InvokeRequired)
            {
                Invoke(new Action<string>(UpdatePickStatusMessage), message);
                return;
            }

            try
            {
                lbl_pickStatusMsg.Text = message;
                lbl_pickStatusMsg.Refresh(); // 즉시 UI 갱신
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdatePickStatusMessage 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// ★★★ 2026-02-05: 실시간 매칭 상세 상태 표시 ★★★
        /// 경과 시간, 집중도, 해시 수를 시각적으로 표시합니다.
        /// </summary>
        private void UpdateMatchingProgress(int elapsedSeconds, double concentration, int hashCount, string state)
        {
            if (lbl_pickStatusMsg == null) return;

            if (InvokeRequired)
            {
                Invoke(new Action<int, double, int, string>(UpdateMatchingProgress), elapsedSeconds, concentration, hashCount, state);
                return;
            }

            try
            {
                // 상태 아이콘 (C# 7.3 호환)
                string icon;
                switch (state)
                {
                    case "searching": icon = "🔍"; break;
                    case "matching": icon = "✅"; break;
                    case "weak": icon = "⚠️"; break;
                    case "fast": icon = "⚡"; break;
                    default: icon = "🎤"; break;
                }

                // 집중도 바 (10칸 기준)
                int barLength = Math.Min(10, (int)(concentration * 10));
                string concentrationBar = new string('█', barLength) + new string('░', 10 - barLength);

                // 형식: "🔍 12초 | [████████░░] 80% | 해시: 450"
                string message = $"{icon} {elapsedSeconds}초 | [{concentrationBar}] {concentration:P0} | 해시: {hashCount}";
                
                lbl_pickStatusMsg.Text = message;
                lbl_pickStatusMsg.Refresh();
            }
            catch { }
        }

        /// <summary>
        /// 영화 핑거프린트를 로드합니다.
        /// </summary>
        private async void LoadMovieFingerprint()
        {
            try
            {
                //var fptFilePath = _fptFile.GetFeatureFilePath();
                // 파일의 존재 확인
                var (found, fptFilePath) = pa.workTab.FptFileInfo.Find_movieFptFileAdapted();
                if (!found)
                {
                    System.Diagnostics.Debug.WriteLine($"LoadMovieFingerprint: 파일이 존재하지 않음: {fptFilePath}");
                    _movieFp = null;
                    UpdateFingerprintFlagColor(false);

                    // 파일이 없을 때 로딩 시간 표시 초기화
                    if (InvokeRequired)
                    {
                        Invoke(new Action(() => { lblLoadingTime.Text = "-"; }));
                    } else {
                        lblLoadingTime.Text = "-";
                    }
                    MessageBox.Show(fptFilePath, "알림", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // 파일 정보 출력
                var fileInfo = new FileInfo(fptFilePath);
                System.Diagnostics.Debug.WriteLine($"LoadMovieFingerprint: 파일 존재 - 크기: {fileInfo.Length} bytes, 확장자: {Path.GetExtension(fptFilePath)}");
                    
                // 로드 시작: 깜빡임 타이머 시작
                _isFingerprintLoading = true;
                _fingerprintLoadTimer?.Start();

                // 로딩 시간 측정 시작
                var loadStopwatch = System.Diagnostics.Stopwatch.StartNew();

                // 백그라운드에서 로드
                _movieFp = await Task.Run(() =>
                {
                    try
                    {
                        if (!File.Exists(fptFilePath))
                        {
                            System.Diagnostics.Debug.WriteLine($"LoadMovieFingerprint: 파일이 존재하지 않습니다: {fptFilePath}");
                            return null;
                        }
                            
                        // 역인덱스 포함하여 로드
                        var result = SFPFM.LoadFingerprintsFromFile(fptFilePath, out var hashToTimestamps);
                            
                        // ★ 핑거프린트 버전 검증 (개선: 여러 샘플 확인) ★
                        if (result != null && result.Count > 0)
                        {
                            // 여러 위치의 해시 메타데이터 확인 (시작, 중간, 끝)
                            int[] checkIndices = { 0, result.Count / 2, result.Count - 1 };
                            int legacyCount = 0;
                            int checkedCount = 0;
                            
                            foreach (int idx in checkIndices)
                            {
                                if (idx >= 0 && idx < result.Count)
                                {
                                    var entry = result[idx];
                                    if (entry?.Hashes != null && entry.Hashes.Count > 0)
                                    {
                                        var sampleHash = entry.Hashes.FirstOrDefault();
                                        if (sampleHash != null)
                                        {
                                            checkedCount++;
                                            if (sampleHash.Frequency1 == 0 && 
                                                sampleHash.Frequency2 == 0 && 
                                                sampleHash.TimeDelta == 0)
                                            {
                                                legacyCount++;
                                            }
                                        }
                                    }
                                }
                            }
                            
                            // 검사한 샘플 중 50% 이상이 메타데이터 없으면 이전 버전으로 판단
                            bool isLegacyFormat = checkedCount > 0 && (legacyCount * 2 >= checkedCount);
                            
                            if (isLegacyFormat)
                            {
                                System.Diagnostics.Debug.WriteLine("⚠️ [경고] 이전 버전의 핑거프린트 파일입니다. 해시 메타데이터(F1/F2/dt)가 없습니다.");
                                System.Diagnostics.Debug.WriteLine("⚠️ 매칭 정확도가 떨어질 수 있습니다. 핑거프린트 재생성을 권장합니다.");
                                System.Diagnostics.Debug.WriteLine($"⚠️ 검사 결과: {checkedCount}개 샘플 중 {legacyCount}개가 메타데이터 없음");
                            }
                            else if (checkedCount > 0)
                            {
                                var firstEntry = result.FirstOrDefault(e => e.Hashes != null && e.Hashes.Count > 0);
                                var sampleHash = firstEntry?.Hashes?.FirstOrDefault();
                                System.Diagnostics.Debug.WriteLine($"✓ 핑거프린트 버전 검증 완료: 메타데이터 포함 (F1={sampleHash?.Frequency1:F0}, F2={sampleHash?.Frequency2:F0})");
                            }
                            
                            // ★★★ 2026-02-03: 해시 문자열 길이(비트 수) 진단 추가 ★★★
                            var hashLengthEntry = result.FirstOrDefault(e => e.Hashes != null && e.Hashes.Count > 0);
                            if (hashLengthEntry?.Hashes?.FirstOrDefault()?.Hash != null)
                            {
                                var sampleHashStr = hashLengthEntry.Hashes.First().Hash;
                                int hashLength = sampleHashStr.Length;
                                bool is64Bit = hashLength == 16;
                                bool is32Bit = hashLength == 8;
                                
                                System.Diagnostics.Debug.WriteLine($"\n★★★ [FPT 해시 형식 진단] ★★★");
                                System.Diagnostics.Debug.WriteLine($"  샘플 해시: '{sampleHashStr}'");
                                System.Diagnostics.Debug.WriteLine($"  해시 길이: {hashLength}자리");
                                
                                if (is64Bit)
                                {
                                    System.Diagnostics.Debug.WriteLine($"  ✓ 64비트 해시 형식 (정상)");
                                }
                                else if (is32Bit)
                                {
                                    System.Diagnostics.Debug.WriteLine($"  ❌ 32비트 해시 형식 - FPT 재생성 필요!");
                                    System.Diagnostics.Debug.WriteLine($"  ❌ Live 매칭이 작동하지 않습니다!");
                                }
                                else
                                {
                                    System.Diagnostics.Debug.WriteLine($"  ⚠️ 알 수 없는 해시 형식 ({hashLength}자리)");
                                }
                                System.Diagnostics.Debug.WriteLine($"★★★ [FPT 해시 형식 진단 끝] ★★★\n");
                            }
                            
                            // 핑거프린트 리스트에서 역인덱스 재구축 (과다 출현 해시 필터링 적용)
                            System.Diagnostics.Debug.WriteLine($"LoadMovieFingerprint: 필터링된 역인덱스 구축 시작 ({result.Count}개 핑거프린트)");
                            // ★ SFPFM.DefaultMaxHashOccurrences 통합 상수 사용 ★
                            var newRvsIndex = SFPFM.BuildFilteredReverseIndex(result, 
                                msg => System.Diagnostics.Debug.WriteLine(msg), 
                                maxHashOccurrences: SFPFM.DefaultMaxHashOccurrences);
                            
                            // ★★★ 2026-02-07: BuildFilteredReverseIndex 반환값 즉시 검증 ★★★
                            System.Diagnostics.Debug.WriteLine($"\n★★★ [BuildFilteredReverseIndex 반환값 즉시 검증] ★★★");
                            System.Diagnostics.Debug.WriteLine($"  반환된 역인덱스 해시 개수: {newRvsIndex?.Count ?? 0}개");
                            if (newRvsIndex != null && newRvsIndex.Count > 0)
                            {
                                // 809초 해시 확인
                                string sample809InNew = null;
                                int count809InNew = 0;
                                foreach (var kvp in newRvsIndex)
                                {
                                    if (kvp.Value.Contains(809))
                                    {
                                        count809InNew++;
                                        if (sample809InNew == null)
                                            sample809InNew = kvp.Key.ToString("X16");
                                    }
                                }
                                System.Diagnostics.Debug.WriteLine($"  newRvsIndex에서 809초 해시: {count809InNew}개, 샘플: 0x{sample809InNew ?? "없음"}");
                                
                                // 0x379BE17736C94ADA 해시 확인
                                ulong testHash = 0x379BE17736C94ADA;
                                bool hasTestHash = newRvsIndex.ContainsKey(testHash);
                                System.Diagnostics.Debug.WriteLine($"  0x379BE17736C94ADA 존재 여부: {hasTestHash}");
                            }
                            
                            // 기존 hashToTimestamps와 비교
                            System.Diagnostics.Debug.WriteLine($"  기존 hashToTimestamps 해시 개수: {hashToTimestamps?.Count ?? 0}개");
                            if (hashToTimestamps != null && hashToTimestamps.Count > 0)
                            {
                                string sample809InOld = null;
                                foreach (var kvp in hashToTimestamps)
                                {
                                    if (kvp.Value.Contains(809))
                                    {
                                        sample809InOld = kvp.Key.ToString("X16");
                                        break;
                                    }
                                }
                                System.Diagnostics.Debug.WriteLine($"  기존 hashToTimestamps의 809초 샘플: 0x{sample809InOld ?? "없음"}");
                            }
                            System.Diagnostics.Debug.WriteLine($"★★★ [검증 끝] ★★★\n");
                            
                            // 덮어쓰기
                            hashToTimestamps = newRvsIndex;
                            System.Diagnostics.Debug.WriteLine($"LoadMovieFingerprint: 필터링된 역인덱스 구축 완료 ({hashToTimestamps?.Count ?? 0}개 해시, 임계값: {SFPFM.DefaultMaxHashOccurrences}회)");
                            
                            // ★★★ 원본 핑거프린트 해시 개수 분석 (Live와 비교용) ★★★
                            int totalOrigHashCount = 0;
                            int totalOrigEntries = result?.Count ?? 0;
                            double totalOrigDurationSec = 0;
                            foreach (var entry in result ?? Enumerable.Empty<FptEntry>())
                            {
                                if (entry.Hashes != null)
                                {
                                    totalOrigHashCount += entry.Hashes.Count;
                                }
                                if (entry.Timestamp > totalOrigDurationSec)
                                {
                                    totalOrigDurationSec = entry.Timestamp;
                                }
                            }
                            double avgHashPerSec = totalOrigDurationSec > 0 ? totalOrigHashCount / totalOrigDurationSec : 0;
                            System.Diagnostics.Debug.WriteLine($"\n★★★ [원본 핑거프린트 해시 개수 분석] ★★★");
                            System.Diagnostics.Debug.WriteLine($"  총 엔트리 수: {totalOrigEntries}개");
                            System.Diagnostics.Debug.WriteLine($"  총 해시 수: {totalOrigHashCount}개");
                            System.Diagnostics.Debug.WriteLine($"  총 길이: {totalOrigDurationSec:F0}초");
                            System.Diagnostics.Debug.WriteLine($"  ★ 초당 평균 해시 수: {avgHashPerSec:F1}개/초");
                            System.Diagnostics.Debug.WriteLine($"  (Live 비교용: Live는 약 1400-1700개/초 생성)");
                            System.Diagnostics.Debug.WriteLine($"★★★ [원본 핑거프린트 해시 개수 분석 끝] ★★★\n");
                            
                            // ★★★ 2026-02-07: 역인덱스 일치성 검증 ★★★
                            System.Diagnostics.Debug.WriteLine($"\n★★★ [역인덱스 일치성 검증] ★★★");
                            int testTimestamp = 809; // 테스트용 타임스탬프
                            var entriesAtTest = result.Where(e => e.Timestamp == testTimestamp).ToList();
                            if (entriesAtTest.Count > 0)
                            {
                                int hashesInFpt = entriesAtTest.Sum(e => e.Hashes?.Count ?? 0);
                                int foundInRvsIndex = 0;
                                int notFoundInRvsIndex = 0;
                                string sampleFptHash = null;
                                ulong sampleFptHashUlong = 0;
                                string sampleRvsHash = null;
                                
                                foreach (var entry in entriesAtTest)
                                {
                                    if (entry.Hashes == null) continue;
                                    foreach (var hash in entry.Hashes.Take(20))
                                    {
                                        ulong hashVal = FingerprintHashData_mp.HexStringToUlong(hash.Hash);
                                        if (sampleFptHash == null)
                                        {
                                            sampleFptHash = hash.Hash;
                                            sampleFptHashUlong = hashVal;
                                        }
                                        if (hashToTimestamps.ContainsKey(hashVal))
                                            foundInRvsIndex++;
                                        else
                                            notFoundInRvsIndex++;
                                    }
                                }
                                
                                // 역인덱스에서 해당 시간대 해시 샘플
                                foreach (var kvp in hashToTimestamps)
                                {
                                    if (kvp.Value.Contains(testTimestamp))
                                    {
                                        sampleRvsHash = kvp.Key.ToString("X16");
                                        break;
                                    }
                                }
                                
                                System.Diagnostics.Debug.WriteLine($"  테스트 시간대: {testTimestamp}초");
                                System.Diagnostics.Debug.WriteLine($"  FPT의 해시 개수: {hashesInFpt}개");
                                System.Diagnostics.Debug.WriteLine($"  역인덱스 존재: {foundInRvsIndex}개, 미존재: {notFoundInRvsIndex}개");
                                System.Diagnostics.Debug.WriteLine($"  FPT 샘플 해시: '{sampleFptHash}' → ulong: {sampleFptHashUlong} (0x{sampleFptHashUlong:X16})");
                                System.Diagnostics.Debug.WriteLine($"  역인덱스 샘플: 0x{sampleRvsHash}");
                                
                                if (foundInRvsIndex == 0 && notFoundInRvsIndex > 0)
                                {
                                    System.Diagnostics.Debug.WriteLine($"  ❌ 심각한 불일치! FPT 해시가 역인덱스에 없음!");
                                }
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"  {testTimestamp}초 엔트리 없음");
                            }
                            System.Diagnostics.Debug.WriteLine($"★★★ [역인덱스 일치성 검증 끝] ★★★\n");
                        }

                        // 역인덱스를 클래스 필드에 저장 (매칭 성능 향상)
                        _movieRvsIndex = hashToTimestamps;

                        System.Diagnostics.Debug.WriteLine(Bases.Diagnose_movieFptConfig(fptFilePath, result, _movieRvsIndex));

                        // ★ 디버그: _movieRvsIndex 저장 확인 ★
                        System.Diagnostics.Debug.WriteLine($"★ _movieRvsIndex 저장됨: {_movieRvsIndex?.Count ?? 0}개 해시 ★");
                        
                        // ★★★ 64비트 해시 형식 진단 (2026-02-02 추가) ★★★
                        if (result != null && result.Count > 0)
                        {
                            var sampleEntry = result.FirstOrDefault(e => e.Hashes != null && e.Hashes.Count > 0);
                            if (sampleEntry != null)
                            {
                                var sampleHash = sampleEntry.Hashes.First();
                                int hashLength = sampleHash.Hash?.Length ?? 0;
                                string hashFormat = hashLength == 16 ? "64비트 (정상)" : hashLength == 8 ? "32비트 (★재생성 필요★)" : $"알 수 없음 ({hashLength}자리)";
                                System.Diagnostics.Debug.WriteLine($"★★★ [원본 fpt 해시 형식 진단] ★★★");
                                System.Diagnostics.Debug.WriteLine($"  샘플 해시: '{sampleHash.Hash}'");
                                System.Diagnostics.Debug.WriteLine($"  해시 길이: {hashLength}자리");
                                System.Diagnostics.Debug.WriteLine($"  형식 판정: {hashFormat}");
                                if (hashLength != 16)
                                {
                                    System.Diagnostics.Debug.WriteLine($"  ⚠️ 경고: 64비트 해시(16자리)가 아닙니다! fpt 파일을 새로 생성해야 합니다.");
                                }
                                System.Diagnostics.Debug.WriteLine($"★★★ [원본 fpt 해시 형식 진단 끝] ★★★");
                            }
                        }

                        // 매칭 엔진 초기화
                        // ★★★ 2026-02-02: 윈도우 크기 증가 (5초 → 15초) ★★★
                        // 5초 윈도우는 해시 수가 적어 노이즈 영향이 큼
                        // 15초 윈도우로 더 많은 해시를 축적하여 정확도 향상
                        _matcher = new RealtimeFingerprintMatcher(
                            _movieRvsIndex,
                            windowSizeSeconds: 15,   // 15초 윈도우 (기존 5초)
                            slideIntervalSeconds: 1  // 1초마다 슬라이드
                        );

                        return result;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"LoadMovieFingerprint: LoadFingerprintsFromFile 예외 - {ex.GetType().Name}: {ex.Message}");
                        if (ex.InnerException != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"  내부 예외: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                        }
                        System.Diagnostics.Debug.WriteLine($"  스택 트레이스: {ex.StackTrace}");
                        return null;
                    }
                });


                // 로딩 시간 측정 완료
                loadStopwatch.Stop();
                TimeSpan loadingTime = loadStopwatch.Elapsed;

                // 로드 완료: 깜빡임 타이머 중지
                _isFingerprintLoading = false;
                _fingerprintLoadTimer?.Stop();

                // 로딩 시간 표시 (mm:ss.ms 형식)
                if (lblLoadingTime != null)
                {
                    string loadingTimeText = FormatLoadingTime(loadingTime);
                    if (InvokeRequired)
                    {
                        Invoke(new Action(() =>
                        {
                            lblLoadingTime.Text = loadingTimeText;
                        }));
                    }
                    else
                    {
                        lblLoadingTime.Text = loadingTimeText;
                    }
                }

                // 로드 성공 여부에 따라 btnFlagFpf 색상 업데이트
                bool isLoaded = _movieFp != null && _movieFp.Count > 0;
                if (isLoaded)
                {
                    // 로드 완료 시 녹색으로 설정
                    UpdateFingerprintFlagColor(true);
                }
                else
                {
                    // 로드 실패 시 회색으로 설정
                    System.Diagnostics.Debug.WriteLine($"LoadMovieFingerprint: 로드 실패 - _movieFp가 null이거나 비어있음");
                    UpdateFingerprintFlagColor(false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadMovieFingerprint: 예외 발생 - {ex.GetType().Name}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"  스택 트레이스: {ex.StackTrace}");
                _movieFp = null;
                
                // 오류 발생 시 깜빡임 타이머 중지
                _isFingerprintLoading = false;
                _fingerprintLoadTimer?.Stop();
                
                UpdateFingerprintFlagColor(false);
            }
        }

        /// <summary>
        /// 핑거프린트 로드 중 깜빡임 타이머 Tick 이벤트 핸들러
        /// </summary>
        private void FingerprintLoadTimer_Tick(object sender, EventArgs e)
        {
            if (btnFlagMovieFpt == null || !_isFingerprintLoading)
            {
                return;
            }

            // UI 스레드에서 실행
            if (InvokeRequired)
            {
                Invoke(new Action(() => FingerprintLoadTimer_Tick(sender, e)));
                return;
            }

            try
            {
                // 깜빡임 효과: 현재 색상과 다른 색상으로 토글
                if (btnFlagMovieFpt.BackColor == SystemColors.ButtonFace)
                {
                    // 회색 -> 노란색 (로딩 중)
                    btnFlagMovieFpt.BackColor = Color.FromArgb(150, 255, 200, 100); // 노란색계통
                }
                else
                {
                    // 노란색 -> 회색
                    btnFlagMovieFpt.BackColor = SystemColors.ButtonFace;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FingerprintLoadTimer_Tick 오류: {ex.Message}");
            }
        }

        /// <summary>
        /// btnFlagFpf 색상을 업데이트합니다.
        /// </summary>
        /// <param name="isLoaded">핑거프린트가 로드되었는지 여부</param>
        private void UpdateFingerprintFlagColor(bool isLoaded)
        {
            if (btnFlagMovieFpt == null)
            {
                return;
            }

            // UI 스레드에서 실행
            if (InvokeRequired)
            {
                Invoke(new Action<bool>(UpdateFingerprintFlagColor), isLoaded);
                return;
            }

            try
            {
                if (isLoaded)
                {
                    // 핑거프린트가 로드되었으면 녹색
                    btnFlagMovieFpt.BackColor = Color.FromArgb(150, 100, 200, 100); // 녹색계통
                }
                else
                {
                    // 핑거프린트가 로드되지 않았으면 회색
                    btnFlagMovieFpt.BackColor = SystemColors.ButtonFace;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateFingerprintFlagColor 오류: {ex.Message}");
                btnFlagMovieFpt.BackColor = SystemColors.ButtonFace;
            }
        }

        private string ComposePickedFilename(string movieID, TimeSpan pickTime, int termMs)
        {
            return SFPci.ComposePickedFilename(movieID, pickTime, termMs, _fptFile.extention, _fptFile.featureDir);
        }
        private bool ConvertToTimeSpan(string hhmmssDotMs, out TimeSpan result)
        {
            return SFPci.ConvertToTimeSpan(hhmmssDotMs, out result);
        }

       
        private float[] PreprocessSamplesForFingerprint(float[] samples, int sampleRate, PreprocessParam pparam, double gateSoftnessMultiplier)
        {
            if (samples == null || samples.Length == 0)
            {
                return samples;
            }

            if (sampleRate <= 0)
            {
                sampleRate = 44100;
            }
   
            // 1) DC 오프셋 제거
            double mean = 0.0;
            for (int i = 0; i < samples.Length; i++)
            {
                mean += samples[i];
            }
            mean /= samples.Length;

            // 2) 1차 하이패스 (저주파/러블 제거)
            double hpCutoffHz = Math.Max(10.0, pparam.hpCutoffHz);
            double rc = 1.0 / (2.0 * Math.PI * hpCutoffHz);
            double dt = 1.0 / sampleRate;
            double hpAlpha = rc / (rc + dt);
            double prevX = 0.0;
            double prevY = 0.0;

            // 3) RMS 계산 (하이패스 적용 후 기준)
            double sumSq = 0.0;
            for (int i = 0; i < samples.Length; i++)
            {
                double x = samples[i] - mean;
                double y = hpAlpha * (prevY + x - prevX);
                prevX = x;
                prevY = y;
                sumSq += y * y;
            }
            double rms = Math.Sqrt(sumSq / samples.Length);

            // 4) 볼륨 정규화 (과도한 증폭 방지)
            double targetRms = Math.Max(0.001, pparam.targetRms);
            const double maxGain = 6.0;
            double gain = rms > 1e-6 ? (targetRms / rms) : 1.0;
            if (gain > maxGain)
            {
                gain = maxGain;
            }

            // 5) 노이즈 바닥 추정 (하이패스 + 다운샘플)
            int step = Math.Max(1, samples.Length / 15000);
            var absSamples = new List<double>();
            prevX = 0.0;
            prevY = 0.0;
            for (int i = 0; i < samples.Length; i += step)
            {
                double x = samples[i] - mean;
                double y = hpAlpha * (prevY + x - prevX);
                prevX = x;
                prevY = y;
                absSamples.Add(Math.Abs(y));
            }
            absSamples.Sort();
            double noiseFloor = absSamples.Count > 0 ? absSamples[(int)(absSamples.Count * 0.2)] : 0.0;

            // 6) 게이트 임계값 + 히스테리시스/엔벌로프 적용
            double baseGateMultiplier = Math.Max(0.1, pparam.baseGateMultiplier);
            if (gateSoftnessMultiplier < 0.1)
            {
                gateSoftnessMultiplier = 0.1;
            }
            double gateThreshold = noiseFloor * baseGateMultiplier / gateSoftnessMultiplier;
            double openThreshold = gateThreshold;
            double closeThreshold = gateThreshold * 0.7;

            double attackMs = Math.Max(0.1, pparam.attackMs);
            double releaseMs = Math.Max(1.0, pparam.releaseMs);
            double attackCoeff = Math.Exp(-1.0 / (sampleRate * attackMs / 1000.0));
            double releaseCoeff = Math.Exp(-1.0 / (sampleRate * releaseMs / 1000.0));
            double envelope = 0.0;
            bool gateOpen = false;

            // 7) 적용 (하이패스 + 정규화 + 노이즈 게이트 + 소프트 리미터)
            float[] processed = new float[samples.Length];
            prevX = 0.0;
            prevY = 0.0;
            double clipDrive = Math.Max(0.1, pparam.clipDrive);
            double tanhDen = Math.Tanh(clipDrive);
            for (int i = 0; i < samples.Length; i++)
            {
                double x = samples[i] - mean;
                double y = hpAlpha * (prevY + x - prevX);
                prevX = x;
                prevY = y;

                double v = y * gain;
                double absV = Math.Abs(v);
                if (absV > envelope)
                {
                    envelope = attackCoeff * (envelope - absV) + absV;
                }
                else
                {
                    envelope = releaseCoeff * (envelope - absV) + absV;
                }

                if (!gateOpen && envelope >= openThreshold)
                {
                    gateOpen = true;
                }
                else if (gateOpen && envelope <= closeThreshold)
                {
                    gateOpen = false;
                }

                if (!gateOpen)
                {
                    v = 0.0;
                }

                // 소프트 리미터
                v = Math.Tanh(v * clipDrive) / tanhDen;
                processed[i] = (float)v;
            }

            return processed;
        }

        private float[] ReadAudioSamples(string audioFilePath, TimeSpan startTime, int termMs)
        {
            using (var reader = new AudioFileReader(audioFilePath))
            {
                // pickTime 위치로 이동 (동적 대기/이동 적용)
                if (startTime < TimeSpan.Zero)
                {
                    startTime = TimeSpan.Zero;
                }
                if (startTime > reader.TotalTime)
                {
                    return Array.Empty<float>();
                }
                reader.CurrentTime = startTime;

                // currentTermMs 동안의 샘플 읽기
                int sampleRate = reader.WaveFormat.SampleRate;
                int channels = reader.WaveFormat.Channels;
                int samplesToRead = (int)(sampleRate * channels * termMs / 1000.0);

                float[] buffer = new float[samplesToRead];
                int samplesRead = reader.Read(buffer, 0, samplesToRead);
                // 읽은 샘플만 반환
                if (samplesRead < samplesToRead)
                {
                    float[] trimmed = new float[samplesRead];
                    Array.Copy(buffer, trimmed, samplesRead);
                    buffer = trimmed;
                }
                // 모노로 변환 (여러 채널인 경우 평균)
                if (channels > 1)
                {
                    float[] mono = new float[buffer.Length / channels];
                    for (int i = 0; i < mono.Length; i++)
                    {
                        float sum = 0;
                        for (int ch = 0; ch < channels; ch++)
                        {
                            sum += buffer[i * channels + ch];
                        }
                        mono[i] = sum / channels;
                    }
                    return mono;
                }

                return buffer;
            }
        }

        /// <summary>
        /// 재생 중인 오디오의 핑거프린트를 생성하고 저장합니다.
        /// </summary>
        private async Task<bool> SavePickedFingerprint(PickAudioFpParam pickParam, int termMs)
        {
            try
            {
                // 오디오 파일이 로드되어 있는지 확인
                if (_audioFileReader == null || string.IsNullOrWhiteSpace(_fptFile.mvAudioFile))
                {
                    MessageBox.Show("재생 중인 오디오가 없습니다.", "오디오 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                // 오디오 파일 경로
                string audioFilePath = Path.Combine(_fptFile.movieFolder, _fptFile.mvAudioFile);
                if (!File.Exists(audioFilePath))
                {
                    MessageBox.Show("오디오 파일을 찾을 수 없습니다.", "파일 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                // 영화 ID 가져오기
                string movieID = lblCurrMovieId?.Text ?? "미등록";
                if (movieID == "미등록" || string.IsNullOrWhiteSpace(movieID))
                {
                    MessageBox.Show("영화 ID가 설정되지 않았습니다.", "영화 ID 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                // 핑거프린트 생성
                int audioSampleRate = _audioFileReader.WaveFormat.SampleRate;

                // fptFile에 설정된 FFT/Hop 파라미터 사용 (없으면 기본값 사용)
                int fftSize = pickParam.fptCfg.FFTSize;
                int hopSize = pickParam.fptCfg.HopSize;

                // ExtractOriginalFPAsync와 동일하게 FFT/Hop 크기 보정 (2의 거듭제곱 및 hop < fft 보장)
                int power = 1;
                while (power < fftSize) power <<= 1;
                fftSize = power;

                power = 1;
                while (power < hopSize) power <<= 1;
                hopSize = power;

                if (hopSize >= fftSize)
                {
                    hopSize = fftSize / 2;
                }

                // ★★★ 중요: pickParam에 실제 오디오 설정 적용 ★★★
                // 원본 핑거프린트와 동일한 설정을 사용해야 해시가 일치함
                pickParam.sampleRate = audioSampleRate;
                pickParam.fptCfg.FFTSize = fftSize;
                pickParam.fptCfg.HopSize = hopSize;

                // 4단계: 동적 윈도우 확장 및 대기
                // ★ 핵심: 최소 윈도우 크기는 HashTimeWindow 이상이어야 함 ★
                // 원본 핑거프린트는 HashTimeWindow(기본 3초) 내에서 Peak 쌍을 생성하므로,
                // 라이브에서도 동일한 크기 이상의 윈도우가 필요함
                int hashTimeWindowMs = pickParam.fptCfg.HashTimeWindow * 1000; // 초 → ms 변환
                int MinWindowSizeMs = Math.Max(hashTimeWindowMs, 3000); // 최소 HashTimeWindow 또는 3초
                const int WindowExpansionStepMs = 500; // 구간 확장 단계 (ms) - 더 큰 단계로 변경
                const int MinPeaksRequired = 5; // 5; // 최소 Peak 개수
                double qualityThreshold = pa.workTab.pickParam.QualityThreshold; // 최소 허용 품질 점수
                bool UseDynamicWindowExpansion = pickParam.adaptiveDynamic; // 동적 윈도우 확장 사용 여부

                List<FptEntry> fingerprints = null;
                int actualTermMs = termMs;

                if (UseDynamicWindowExpansion)
                {
                    // 동적 윈도우 확장 사용
                    // ★ 매칭을 위해 HashTimeWindow 이상의 윈도우로 시작 ★
                    int currentTermMs = MinWindowSizeMs; // HashTimeWindow(3초) 이상부터 시작
                    bool lowConcnt = _lastOffsetConcentration.HasValue && _lastOffsetConcentration.Value < pickParam.offsetConcntThreshold;
                    int dynamicMaxWindowMs = lowConcnt ? pickParam.maxWindowSizeMsLowOffset : pickParam.maxWindowSizeMs;
                    int maxTermMs = (termMs > dynamicMaxWindowMs) ? termMs : dynamicMaxWindowMs; // 설정된 최대 구간까지 확장
                    double gateSoftnessMultiplier = lowConcnt ? pickParam.gateSoftnessMultiplierLowOffset : pickParam.gateSoftnessMultiplier;
                    int shiftedMs = 0;
                    int maxShiftMs = maxTermMs; // 최대 대기/이동 시간
                    TimeSpan currentStartTime = pickParam.pickTime;
                    TimeSpan actualPickTime = pickParam.pickTime;

                    // 무음 감지용 상수
                    const double SilenceThreshold = 0.01; // RMS 임계값 (무음 판단)
                    const int MinPeaksForValidSignal = 2; // 유효 신호로 판단할 최소 peak 수
                    const int MaxIterations = 20; // ★ 최대 반복 횟수 제한 ★
                    
                    // 상태 표시: 동적 윈도우 시작
                    lbl_pickStatusMsg.Text = $"동적 윈도우 시작: {currentTermMs}ms (HashTimeWindow: {hashTimeWindowMs}ms)";
                    System.Diagnostics.Debug.WriteLine($"[동적 윈도우] 시작: MinWindow={MinWindowSizeMs}ms, MaxWindow={maxTermMs}ms, MaxShift={maxShiftMs}ms");
                    int iterationCount = 0;
                    string msg = "";

                    while (true)
                    {
                        iterationCount++;
                        
                        // ★ 최대 반복 횟수 초과 시 종료 ★
                        if (iterationCount > MaxIterations)
                        {
                            msg = $"최대 반복 횟수 도달 ({MaxIterations}회) - 현재 상태로 추출";
                            System.Diagnostics.Debug.WriteLine($"[동적 윈도우] {msg}");
                            actualTermMs = currentTermMs;
                            break;
                        }
                        // 상태 표시: 현재 진행 상태
                        lbl_pickStatusMsg.Text = msg + $" ~  구간:{currentTermMs}ms, 이동:{shiftedMs}ms";
                        await Task.Delay(1); // UI 갱신을 위한 짧은 대기

                        // 현재 구간의 샘플 읽기 (원본과 동일한 방식으로 읽기)
                        // ★ 경계 오버랩 포함: HashTimeWindow × 2 만큼 추가 읽기 ★
                        // 원본 핑거프린트는 전체 파일에서 Peak 쌍을 생성하므로, 
                        // 라이브에서도 구간 경계에서 동일한 Peak 쌍이 선택되도록 오버랩 필요
                        // 동적 시간 윈도우가 최대 6초(HashTimeWindow+3)까지 확장될 수 있으므로
                        // 안전하게 HashTimeWindow × 2 만큼 오버랩 추가
                        int boundaryOverlapMs = hashTimeWindowMs * 2; // HashTimeWindow × 2 (기본 6초)
                        int extendedTermMs = currentTermMs + boundaryOverlapMs;
                        
                        // ★ 디버그 로그: 경계 오버랩 적용 확인 ★
                        if (iterationCount == 1) // 첫 번째 반복에서만 로그 출력
                        {
                            System.Diagnostics.Debug.WriteLine($"[동적 윈도우] 경계 오버랩 적용: 요청={currentTermMs}ms, 오버랩={boundaryOverlapMs}ms, 실제읽기={extendedTermMs}ms");
                        }

                        // ★ 그리드 정렬 (HopSize Grid Alignment) - Sample Index 기반 정밀 보정 ★
                        // 사용자의 요청에 따라 HopSize 그리드에 정확히 맞추어 시작 시간 보정
                        // 원본 핑거프린트는 N * HopSize 시점마다 FFT를 수행하므로,
                        // 임의 추출 시에도 이 그리드에 맞춰야 위상 오차(Phase Shift)를 최소화하여 해시 정확도 향상
                        if (pickParam.fptCfg.HopSize > 0 && pickParam.sampleRate > 0)
                        {
                            long startSampleIndex = (long)(currentStartTime.TotalSeconds * pickParam.sampleRate);
                            hopSize = pickParam.fptCfg.HopSize;
                            
                            // HopSize 배수로 정렬 (내림)
                            long alignedSampleIndex = (startSampleIndex / hopSize) * hopSize;

                            if (startSampleIndex != alignedSampleIndex)
                            {
                                double originalTime = currentStartTime.TotalSeconds;
                                double alignedTime = (double)alignedSampleIndex / pickParam.sampleRate;
                                TimeSpan correction = TimeSpan.FromSeconds(originalTime - alignedTime);
                                
                                System.Diagnostics.Debug.WriteLine($"[그리드 정렬] Sample: {startSampleIndex} -> {alignedSampleIndex} (Hop:{hopSize}), Time: {originalTime:F4}s -> {alignedTime:F4}s (보정: -{correction.TotalMilliseconds:F3}ms)");
                                
                                currentStartTime = TimeSpan.FromSeconds(alignedTime);
                            }
                        }
                        
                        int readSampleRate = 0;
                        double[] samples = await Task.Run(() => 
                            SFPFM.ReadAudioSamplesForLiveDouble(audioFilePath, currentStartTime, extendedTermMs, out readSampleRate));
                        if (samples == null || samples.Length == 0)
                        {
                            break; // 파일 끝
                        }
                        // 샘플 레이트가 읽혔으면 pickParam에 업데이트
                        if (readSampleRate > 0)
                        {
                            audioSampleRate = readSampleRate;
                            pickParam.sampleRate = audioSampleRate;
                        }

                        // ★ 무음 구간 감지 (RMS 기반) ★
                        double rms = 0;
                        for (int i = 0; i < samples.Length; i++) { rms += samples[i] * samples[i]; }
                        rms = Math.Sqrt(rms / samples.Length);
                        bool isSilence = rms < SilenceThreshold;

                        if (isSilence)
                        {
                            // 무음 구간 → 시간 이동 (구간 확장 없이)
                            msg = $"[{iterationCount}] 무음 감지 → 시간 이동 +{WindowExpansionStepMs}ms";
                            shiftedMs += WindowExpansionStepMs;
                            if (shiftedMs >= maxShiftMs)
                            {
                                // 최대 대기 시간 도달 → 현재 상태로 추출 (실패 가능)
                                msg = $"최대 대기 시간 도달 ({maxShiftMs}ms)";
                                actualTermMs = currentTermMs;
                                break;
                            }
                            currentStartTime = pickParam.pickTime + TimeSpan.FromMilliseconds(shiftedMs);
                            actualPickTime = currentStartTime;
                            currentTermMs = MinWindowSizeMs; // ★ 구간 크기 리셋 ★
                            continue; // 다음 위치에서 다시 시도
                        }

                        // 1단계: 1.신호 품질 메트릭 계산(SNR 동적 추정)
                        // 1단계: 2.peak 유효성을 판단하는 추가 지표(Spectral Centroid & Entropy 계산)
                        // 2단계: 적응형 Threshold 동적 조정(Gaussian 평활을 통한 가우시안 스무딩)
                        // 3단계: 1.Quality-Driven Peak Detection
                        // 3단계: 2.품질 점수 계산
                        // 5단계: Constellation Map 생성 (Adaptive Fan-out)
                        // 핑거프린트 생성 (품질 점수 포함)

                        // 전처리 및 핑거프린트 생성
                        // (Double Precision에서는 전처리 로직이 float 기반이라 비활성화 유지, 원본 매칭도 Raw 데이터 사용함)
                        //samples = PreprocessSamplesForFingerprint(samples, audioSampleRate, pickParam.PP, gateSoftnessMultiplier);
                        double qualityScore = 0.0;
                        int peakCount = 0;
                        fingerprints = await Task.Run(() =>
                            SFPFM.GenerateSampleFingerprintWithQuality(samples, pickParam, out qualityScore, out peakCount));
                        // 6단계: Offset Concentration 계산
                        bool hasFingerprints = fingerprints != null && fingerprints.Count > 0;
                        double curConcnt = 0.0;
                        if (hasFingerprints)
                        {
                            System.Diagnostics.Debug.WriteLine(Bases.Diagnose_liveFptConfig(currentStartTime, pickParam, samples));
                            
                            var (result, offsetConcentration) = SFPFM.CalcOffsetConcentration(fingerprints, _movieRvsIndex, maxHashOccurrences: SFPFM.DefaultMaxHashOccurrences, originalFpts: _movieFp);
                            if (result)
                            {
                                curConcnt = offsetConcentration;
                                _lastOffsetConcentration = curConcnt;
                            }

                            // ★ 해시 매칭 진단 (디버그용) ★
                            if (curConcnt < 0.5) // 집중도가 낮을 때만 진단
                            {
                                // 실제 오디오 위치 (초) 전달, 원본 핑거프린트도 전달
                                int actualAudioPosition = (int)currentStartTime.TotalSeconds;
                                SFPFM.DiagnoseHashMismatch(fingerprints, _movieRvsIndex, 
                                    diagMsg => System.Diagnostics.Debug.WriteLine(diagMsg),
                                    actualAudioPosition,
                                    _movieFp);  // 원본 핑거프린트 전달
                                
                                // ★ 해시 생성 비교 진단 추가 ★
                                SFPFM.DiagnoseHashGenerationDifference(audioFilePath, currentStartTime, currentTermMs,
                                    diagMsg => System.Diagnostics.Debug.WriteLine(diagMsg));
                            }
                        }

                        // ★ 유효 신호가 아니면 시간 이동 (무음은 아니지만 신호가 약한 구간) ★
                        if (peakCount < MinPeaksForValidSignal)
                        {
                            // peak가 너무 적음 → 무음과 유사하게 처리
                            msg = $"[{iterationCount}] 저품질 신호 (peak:{peakCount}) → 시간 이동";
                            shiftedMs += WindowExpansionStepMs;
                            if (shiftedMs >= maxShiftMs)
                            {
                                msg = $"최대 대기 시간 도달 ({maxShiftMs}ms)";
                                actualTermMs = currentTermMs;
                                break;
                            }
                            currentStartTime = pickParam.pickTime + TimeSpan.FromMilliseconds(shiftedMs);
                            actualPickTime = currentStartTime;
                            currentTermMs = MinWindowSizeMs; // 구간 크기 리셋
                            continue;
                        }

                        // ★ 품질 + 매칭률 모두 판단 ★
                        bool qualityOk = qualityScore >= qualityThreshold && peakCount >= MinPeaksRequired;
                        bool matchingOk = hasFingerprints && curConcnt >= 0.3; // 0.1; // 임의 위치 추출 시 분산 고려하여 0.1로 완화

                        if (qualityOk && matchingOk)
                        {
                            // 품질도 좋고 매칭률도 좋음 → 즉시 추출
                            msg = $"✓ 품질/매칭 OK! (Q:{qualityScore:F2}, P:{peakCount}, C:{curConcnt:F2})";
                            actualTermMs = currentTermMs;
                            break;
                        }

                        // 품질은 좋지만 매칭률이 낮은 경우
                        if (qualityOk && !matchingOk)
                        {
                            // ★ [신규] 어려운 구간 조기 감지: RMS가 낮거나 오프셋 집중도가 매우 낮으면 시간 이동 우선 ★
                            // 조건: (1) RMS < 임계값 또는 (2) 오프셋 집중도 < 30% //10%
                            bool isDifficultByRMS = SFPFM.IsDifficultSegment(rms);
                            bool isDifficultByConcentration = curConcnt < 0.30;  // 0.10; // 10% 미만이면 어려운 구간
                            
                            if ((isDifficultByRMS || isDifficultByConcentration) && shiftedMs < maxShiftMs)
                            {
                                string reason = isDifficultByRMS ? $"RMS:{rms:F4}" : $"집중도:{curConcnt:F2}";
                                msg = $"[{iterationCount}] 어려운 구간 감지 ({reason}) → 시간 이동 우선";
                                System.Diagnostics.Debug.WriteLine($"[동적 윈도우] {msg} (+{WindowExpansionStepMs}ms)");
                                shiftedMs += WindowExpansionStepMs;
                                currentStartTime = pickParam.pickTime + TimeSpan.FromMilliseconds(shiftedMs);
                                actualPickTime = currentStartTime;
                                currentTermMs = MinWindowSizeMs; // 구간 크기 리셋
                                continue; // 다음 위치에서 다시 시도
                            }
                            
                            // RMS와 집중도 모두 충분하면 기존 로직: 구간 확장으로 더 많은 해시 확보
                            if (currentTermMs < maxTermMs)
                            {
                                msg = $"[{iterationCount}] 매칭률 부족 (C:{curConcnt:F2}, RMS:{rms:F4}) → 구간 확장";
                                System.Diagnostics.Debug.WriteLine($"[동적 윈도우] {msg}, 현재:{currentTermMs}ms → {currentTermMs + WindowExpansionStepMs}ms");
                                currentTermMs += WindowExpansionStepMs;
                                if (currentTermMs > maxTermMs) currentTermMs = maxTermMs;
                                continue;
                            }
                        }

                        // 품질이 낮으면 구간 확장
                        if (!qualityOk && currentTermMs < maxTermMs)
                        {
                            msg = $"[{iterationCount}] 품질 부족 (Q:{qualityScore:F2}) → 구간 확장 {currentTermMs}→{currentTermMs + WindowExpansionStepMs}ms";
                            System.Diagnostics.Debug.WriteLine($"[동적 윈도우] {msg}");
                            currentTermMs += WindowExpansionStepMs;
                            if (currentTermMs > maxTermMs) currentTermMs = maxTermMs;
                            continue;
                        }

                        // ★ 구간이 최대에 도달하면 현재 상태로 추출 (시간 이동 없이) ★
                        // 매칭률이 낮더라도 최대 구간에서 추출하여 사용자가 결과를 확인할 수 있도록 함
                        msg = $"최대 구간 도달 ({currentTermMs}ms) - 현재 상태로 추출 (C:{curConcnt:F2}, Q:{qualityScore:F2})";
                        System.Diagnostics.Debug.WriteLine($"[동적 윈도우] {msg}");
                        actualTermMs = currentTermMs;
                        break;  // ★ 루프 종료 ★
                    } // end of while

                    pickParam.pickTime = actualPickTime;
                }
                else
                {
                    //// 동적 윈도우 확장 미사용 (기존 방식)
                    //lbl_pickStatusMsg.Text = $"핑거프린트 생성 중... ({termMs}ms)";
                    //int readSampleRate = 0;
                    //float[] samples = await Task.Run(() => 
                    //    SFPFM.ReadAudioSamplesForLive(audioFilePath, pickParam.pickTime, termMs, out readSampleRate));

                    //if (samples == null || samples.Length == 0)
                    //{
                    //    lbl_pickStatusMsg.Text = "오류: 샘플 읽기 실패";
                    //    MessageBox.Show("오디오 샘플을 읽을 수 없습니다.", "읽기 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    //    return false;
                    //}
                    
                    //// 샘플 레이트 업데이트
                    //if (readSampleRate > 0)
                    //{
                    //    audioSampleRate = readSampleRate;
                    //    pickParam.sampleRate = readSampleRate;
                    //}

                    //samples = PreprocessSamplesForFingerprint(samples, audioSampleRate, pickParam.PP, pickParam.gateSoftnessMultiplier);
                    //fingerprints = await Task.Run(() => SFPFM.GenerateLiveFingerprint(samples, pickParam));
                    //actualTermMs = termMs;
                } // end of dynamic window check

                if (fingerprints == null || fingerprints.Count == 0)
                {
                    lbl_pickStatusMsg.Text = "오류: 핑거프린트 생성 실패";
                    MessageBox.Show("핑거프린트를 생성할 수 없습니다.", "생성 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                // 핑거프린트 저장
                lbl_pickStatusMsg.Text = $"저장 중... (해시:{fingerprints.Sum(f => f.Hashes?.Count ?? 0)}개)";
                bool hashOnly = _fptFile.mvHashOnly;
                string filePath = ComposePickedFilename(movieID, pickParam.pickTime, actualTermMs);

                // MessagePack 형식으로 저장 (SFPFM의 MessagePack 저장 메서드 사용)
                await Task.Run(() => SFPFM.SavePickedFptsToFileMessagePack(
                    fingerprints, filePath, audioSampleRate, 1, // Channels: 모노
                    TimeSpan.FromMilliseconds(actualTermMs), hashOnly));

                // dgvPickedFeatures에 추가
                AddPickedFeatureToGrid(filePath, pickParam.pickTime, termMs);

                lbl_pickStatusMsg.Text = $"✓ 저장 완료 ({actualTermMs}ms, 해시:{fingerprints.Sum(f => f.Hashes?.Count ?? 0)}개)";
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"핑거프린트 저장 중 오류가 발생했습니다.\n{ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        } // end of SavePickedFingerprint()

        /// <summary>
        /// 폴더에 있는 모든 *.fp.mpack 파일을 dgvPickedFeatures에 등록합니다.
        /// </summary>
        private void LoadExistingPickedFingerprints()
        {
            // UI 스레드에서만 실행되도록 보장
            if (InvokeRequired)
            {
                Invoke(new Action(LoadExistingPickedFingerprints));
                return;
            }

            // 폼이 dispose되었거나 dispose 중이면 무시
            if (IsDisposed || Disposing)
            {
                return;
            }

            try
            {
                // 핑거프린트 디렉토리 확인
                if (string.IsNullOrWhiteSpace(_fptFile.featureDir) || !Directory.Exists(_fptFile.featureDir))
                {
                    return;
                }

                // *.fp.json 및 *.fp.mpack, *.fp.messagepack 파일 찾기
                var fpFiles = new List<string>();
                fpFiles.AddRange(Directory.GetFiles(_fptFile.featureDir, "*.fp.mpack"));
                
                if (fpFiles.Count == 0)
                {
                    return;
                }

                // 각 파일을 dgvPickedFeatures에 추가
                foreach (string filePath in fpFiles)
                {
                    try
                    {
                        // 파일명에서 pickTime과 termMs 추출
                        string fileName = Path.GetFileName(filePath); // "movieID_pickTime_term.fp.json" 또는 "movieID_pickTime_term.fp.mpack"
                        
                        // 확장자 제거 (.fp.mpack 처리)
                        if (fileName.EndsWith(".fp.mpack", StringComparison.OrdinalIgnoreCase))
                        {
                            fileName = fileName.Substring(0, fileName.Length - 9); // ".fp.mpack" 제거
                        }
                        else
                        {
                            // 알 수 없는 확장자는 건너뜀
                            continue;
                        }
                        
                        // 파일명 형식: movieID_pickTime_term
                        // 예: "123_123456_3000"
                        string[] parts = fileName.Split('_');
                        if (parts.Length >= 3)
                        {
                            // 형식 지원 (movieID_pickTimeMs_term)
                            // pickTime과 termMs 추출 (마지막 두 부분)
                            if (long.TryParse(parts[parts.Length - 2], out long pickTimeMs) &&
                                int.TryParse(parts[parts.Length - 1], out int termMs))
                            {
                                TimeSpan pickTime = TimeSpan.FromMilliseconds(pickTimeMs);
                                AddPickedFeatureToGrid(filePath, pickTime, termMs);
                            }
                            else
                            {
                                // 파싱 실패 시 기본값 사용
                                AddPickedFeatureToGrid(filePath, TimeSpan.Zero, 3000);
                            }
                        }
                        else
                        {
                            // 형식이 맞지 않으면 기본값 사용
                            AddPickedFeatureToGrid(filePath, TimeSpan.Zero, 3000);
                        }
                    }
                    catch (Exception ex)
                    {
                        // 개별 파일 로드 실패는 무시하고 계속 진행
                        System.Diagnostics.Debug.WriteLine($"핑거프린트 파일 로드 오류 ({filePath}): {ex.Message}");
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // 폼이나 컨트롤이 dispose된 경우 무시
            }
            catch (InvalidOperationException)
            {
                // 컨트롤이 유효하지 않은 경우 무시
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"기존 핑거프린트 로드 오류: {ex.Message}");
            }
        }


        /// <summary>
        /// 프로필에서 스니펫 길이 설정을 로드합니다.
        /// </summary>
        private void LoadSnippetLengthFromProfile()
        {
            if (cboSnippetTermMs == null || pa == null || pa.prof == null)
            {
                return;
            }


            var savedValue = pa.prof.GetItem(PDN.S_PARAMS, PDN.E_SIMILTEST, PDN.I_MOVIEPICKTERM);
            if (savedValue.bValid && !string.IsNullOrWhiteSpace(savedValue.sValue))
            {
                // 저장된 값과 일치하는 항목 찾기
                for (int i = 0; i < cboSnippetTermMs.Items.Count; i++)
                {
                    if (cboSnippetTermMs.Items[i].ToString() == savedValue.sValue)
                    {
                        // 이벤트 발생 없이 선택 (이벤트 핸들러가 설정되기 전이므로)
                        cboSnippetTermMs.SelectedIndex = i;
                        break;
                    }
                }
            }
            else
            {
                // 저장된 값이 없으면 기본값 "3000" 설정
                int defaultIndex = -1;
                for (int i = 0; i < cboSnippetTermMs.Items.Count; i++)
                {
                    if (cboSnippetTermMs.Items[i].ToString() == "3000")
                    {
                        defaultIndex = i;
                        break;
                    }
                }
                if (defaultIndex >= 0)
                {
                    cboSnippetTermMs.SelectedIndex = defaultIndex;
                }
            }
            
            var onoff = pa.prof.GetItem(PDN.S_PARAMS, PDN.E_SIMILTEST, PDN.I_DYNAMIC);
            chkAdaptiveDynamic.Checked = (onoff.bValid && onoff.sValue == "On") ? true : false;
        }

        /// <summary>
        /// 프로필에서 스니펫 길이 값을 읽어 반환합니다. 없으면 기본값 3000을 반환합니다.
        /// </summary>
        private int GetSnippetLengthFromProfile()
        {
            var savedValue = pa.prof.GetItem(PDN.S_PARAMS, PDN.E_SIMILTEST, PDN.I_MOVIEPICKTERM);
            if (savedValue.bValid && !string.IsNullOrWhiteSpace(savedValue.sValue))
            {
                if (int.TryParse(savedValue.sValue, out int termMs))
                {
                    return termMs;
                }
            }

            return 1000; // 기본값
        }

        /// <summary>
        /// cboSnippetLength 선택 변경 이벤트 핸들러 - 프로필에 저장
        /// </summary>
        private void CboSnippetLength_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cboSnippetTermMs == null || cboSnippetTermMs.SelectedItem == null)
            {
                return;
            }

            string selectedValue = cboSnippetTermMs.SelectedItem.ToString();
            pa.prof.WriteItem(PDN.S_PARAMS, PDN.E_SIMILTEST, PDN.I_MOVIEPICKTERM, selectedValue);
            pa.prof.Write_DataToFile();
        }

        private void chkAdaptiveDynamic_Click(object sender, EventArgs e)
        {
            //string onoff = (chkAdaptiveDynamic.Checked) ? "On" : "Off";
            //pa.prof.WriteItem(PDN.S_PARAMS, PDN.E_SIMILTEST, PDN.I_DYNAMIC, onoff);
            //pa.prof.Write_DataToFile();
        }


    }
}


