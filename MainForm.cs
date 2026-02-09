using AppBase;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using AudioViewStudio.Analysis;

namespace AudioViewStudio
{
    public partial class MainForm : Form
    {
        DbServerInfo dbInfo = new DbServerInfo();
        public Profile prof = new Profile();
        Point mainFormLocation = new Point(); // WinForm의 위치
        ConfigFormSize configSize = new ConfigFormSize(); // WinForm의 사이즈를 관리하는 객체

        private readonly MovieData movieData = new MovieData();
        // 서버에서 관리하는 영화 ID (현재 작업 컨텍스트 기준)
        private int? currentServerMovieId;
        private bool serverConnected = false; // 서버 연결된 상태 flag
        private bool isRefreshingChecksums = false; // 체크섬 갱신 중 플래그 (재귀 호출 방지)
        // 로컬 MovieData ExtraAttribute 키
        private const string ExtraKeyServerMovieId   = "ServerMovieId";
        private const string ExtraKeyLastMovieAudio  = "LastMovieAudio";
        private const string ExtraKeyLastWaveFile    = "LastWaveFile";

        // UI 입력 정보 관리 객체
        private UserEditingInfo userEditingInfo;

        private string currentMovieFolderName;
        private bool suppressMovieFolderTextChange;
        private CancellationTokenSource featureExtractionCts;
        private AudioFeatures.PauseTokenSource featureExtractionPauseToken;
        private bool featureExtractionPaused;
        private CancellationTokenSource metadataCts;
        private DateTime featureExtractionStartTime;
        private double lastLoggedProgress = -10d;
        private AudioFeatures.AudioMetadata currentAudioMetadata;
        private bool suppressFeatureComboEvents;
        private bool audioConversionInProgress;
        private CancellationTokenSource audioConversionCts;
        private readonly Label lblFeatureStatus = new Label();
        private readonly Dictionary<AudioViewFileCategory, ListViewItem> audioViewFileItems =
            new Dictionary<AudioViewFileCategory, ListViewItem>();
        private readonly Dictionary<TextBox, AudioViewFileCategory> audioViewTextBoxMap =
            new Dictionary<TextBox, AudioViewFileCategory>();
        private readonly Dictionary<AudioViewFileCategory, string> audioViewLocalPathOverrides =
            new Dictionary<AudioViewFileCategory, string>();
        private string currentMovieImagePath = null;   // 현재 작업 영화에 대해 로컬에서 선택된(또는 다운로드된) 이미지 경로
        private string serverImagePath = null;         // 서버에서 받아온 기준 이미지 경로
        private string serverImageChecksum = null;     // 서버에서 받아온 기준 이미지 체크섬
        private enum AudioViewFileCategory
        {
            MovieAudioOrg, 
            MovieAudioWave,
            Feature,
            NarrationKo,
            NarrationEn,
            SubtitleKo,
            SubtitleEn
        }


        public MainForm()
        {
            // Thread.cs GetCurrentThreadNative() 크래시 방지: InitializeComponent 전에 메모리 정리
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                GC.WaitForPendingFinalizers();
            }
            catch
            {
                // GC 실패해도 계속 진행
            }

            try
            {
                InitializeComponent();
            }
            catch (OutOfMemoryException)
            {
                // InitializeComponent 실패 시 사용자에게 알림
                MessageBox.Show(
                    "메모리가 부족하여 폼을 초기화할 수 없습니다.\n\n" +
                    "다음을 시도해보세요:\n" +
                    "1. 다른 프로그램을 종료하여 메모리를 확보하세요.\n" +
                    "2. 컴퓨터를 재시작하세요.",
                    "메모리 부족",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                throw; // 예외를 다시 던져서 Application.Run에서 처리
            }

            configSize.szWinForm = this.Size; // backup the original window size

            string sIniFile = System.Windows.Forms.Application.StartupPath + "\\AudioViewStudio.ini";
            //ValidString auth = Prof.Set_InfoFile(sIniFile, true, true, true, new Service("ZipBack", "20251231"));
            //if (!auth.bValid) { MessageBox.Show(auth.sValue); }
            bool bValid = prof.Set_fileName(sIniFile, true, false, true);

            // 버튼 클릭 이벤트 핸들러 등록
            this.btn_popSettingForm.Click += Btn_popSettingForm_Click;
            this.btn_popSearchForm.Click += Btn_popSearchForm_Click;
            // btnConvertToWave와 btnCancelConvert는 이제 SimilarityTabControl에 있으므로
            // InitializeSimilarityAnalysisUI에서 연결됨

            // DataGridView 초기화
            InitializeMovieDataGrid();
            ResetMovieForm();

            // 영화 목록 더블클릭 시 서버 영화 로딩
            this.dgv_MovieList.CellDoubleClick += Dgv_MovieList_CellDoubleClick;

            // 영화 제목 변경 시 현재 영화 라벨도 함께 갱신
            if (txtMovieTitle != null)
            {
                txtMovieTitle.TextChanged += (s, e) => 
                {
                    UpdateCurrentMovieTitleDisplay();
                    CheckMetadataChanges();
                };
            }

            // 메타데이터 필드 변경 감지 이벤트 연결
            if (txtStudioName != null)
            {
                txtStudioName.TextChanged += (s, e) => CheckMetadataChanges();
            }
            if (txtDirector != null)
            {
                txtDirector.TextChanged += (s, e) => CheckMetadataChanges();
            }
            if (cmbGenre != null)
            {
                cmbGenre.SelectedIndexChanged += (s, e) => CheckMetadataChanges();
            }
            if (nudReleaseYear != null)
            {
                nudReleaseYear.ValueChanged += (s, e) => CheckMetadataChanges();
            }
            if (txtMovieFolderName != null)
            {
                txtMovieFolderName.TextChanged += (s, e) => 
                {
                    if (!suppressMovieFolderTextChange)
                    {
                        CheckMetadataChanges();
                    }
                };
            }

            //movieData = new MovieData();
            InitializeMovieTitleInteractions();
            
            // Thread.cs GetCurrentThreadNative() 크래시 방지: 초기화 메서드 호출 전에 메모리 정리
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                GC.WaitForPendingFinalizers();
            }
            catch { }
            
            InitializeSimilarityAnalysisUI(); // workTabControl 초기화 먼저
            InitializeFeatureExtractionUI(); // workTabControl 초기화 후 호출
            InitializeAudioViewFilesPanel();
            
            // UserEditingInfo 초기화
            if (workTab != null)
            {
                userEditingInfo = new UserEditingInfo(movieData, workTab, (suppress) => suppressFeatureComboEvents = suppress);
            }
            
            RestoreLastMovieWork();
            UpdateCurrentMovieTitleDisplay();
            UpdateCurrentFolderDisplay();
            
            // btnPickMovieImage 활성화
            if (btnPickMovieImage != null)
            {
                btnPickMovieImage.Enabled = true;
            }

            // 버튼 아이콘 설정
            SetupButtonIcons();
        }
        private async void MainForm_Load(object sender, EventArgs e)
        {
            // MainForm Location
            List<string> pos = prof.GetItemParams(PDN.S_SYSTEM, PDN.E_MAINFORM, PDN.I_LOCATION);
            if (pos.Count >= 2)
            {
                mainFormLocation.X = Convert.ToInt32(pos[0]); 
                mainFormLocation.Y = Convert.ToInt32(pos[1]);
                this.Location = mainFormLocation;
            }
            // WinForm Size
            List<string> sz = prof.GetItemParams(PDN.S_SYSTEM, PDN.E_MAINFORM, PDN.I_SIZE);
            if (sz.Count >= 2) { configSize.szCuzedForm = new Size(Convert.ToInt32(sz[0]), Convert.ToInt32(sz[1])); }
            else { configSize.szCuzedForm = configSize.szWinForm; }
            this.Size = configSize.szCuzedForm;
            // 오디오 추출 파라미터 로드
            var vs = prof.GetString(PDN.S_FINGERPRINT, PDN.E_FPT_PARAMS);
            if (vs.bValid && !string.IsNullOrEmpty(vs.sValue))
            {
                workTab.pickParam.SetParamsFromString(vs.sValue);
                workTab.pickParOrg.Update(workTab.pickParam);

                workTab.Init_UI_withLoaded(workTab.pickParam);
            }

            // panelContent의 폭과 높이를 tabPage2의 Client 영역에 맞춘다.
            AdjustWorkPanelSize();

            // 핑거프린트 관련 콤보박스 초기화 (프로필에서 저장된 이전 값으로 복원)
            InitializeFptComboBoxes(workTab.pickParam);

            // Similarity 탭 내부 섹션 레이아웃을 한 번 더 정리하여,
            // 실행 초기에도 Movie Audio Capture / Similarity Analysis / Simulation
            // 영역이 올바른 순서와 위치로 보이도록 강제한다.
            if (workTab != null)
            {
                workTab.RelayoutContentSections();
            }

            // DB 서버 정보 로드
            List<string> varDb = prof.GetItemParams(PDN.S_SYSTEM, PDN.E_DBSERVER, PDN.I_DBS_ACCESS);
            if (varDb.Count >= 6)
            {
                dbInfo.DbServerIP = varDb[0];
                dbInfo.DbServerPort = varDb[1];
                dbInfo.DbName = varDb[2];
                dbInfo.DbUser = varDb[3];
                dbInfo.DbPassword = varDb[4];
                dbInfo.DbConnectTimeout = varDb[5];

                // 서버 연결 상태 확인
                await CheckServerConnection();

                // 서버 API를 통해 영화 목록 가져오기
                try
                {
                    await LoadMoviesFromServer();
                    UpdateServerStatusLabel("서버 연결됨 - 영화 목록 로드 완료", Color.Green, true);
                }
                catch (Exception ex)
                {
                    UpdateServerStatusLabel($"서버 오류: {ex.Message}", Color.Red);
                    //MessageBox.Show("서버에서 영화 목록을 가져오는데 실패했습니다. 샘플 데이터로 대체합니다.\n" + ex.Message, 
                    //    "서버 연결 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    // 샘플 영화 데이터 로드
                    LoadSampleData();
                }
            }
            else
            {
                UpdateServerStatusLabel("서버 정보가 설정되지 않았습니다. 설정 메뉴에서 서버 정보를 입력하세요.", Color.Orange);
            }

            UpdateAudioViewFilesStatus();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (workTab.pickParam.IsChanged(workTab.pickParOrg))
            {
                //workTab.pickParOrg.Update();
                prof.WriteString(PDN.S_FINGERPRINT, PDN.E_FPT_PARAMS, workTab.pickParam.ParamsToString());
                prof.Write_DataToFile();
            }
            // MainForm 을 닫는다.
            e.Cancel = false;
        }

        private void InitializeMovieTitleInteractions()
        {
            // pnlMovieDropArea와 lblMovieTitle은 삭제되었고,
            // 이제 workTabControl의 pnl_MovieDropArea로 이동됨
            // 이벤트 연결은 ConnectMovieFeatureExtractEvents()에서 처리됨
        }

        private void BindAudioViewFileSources()
        {
            audioViewTextBoxMap.Clear();
            // TxtMovieFile은 Label로 변경되어 RegisterAudioViewTextBox에서 제외
            // Label은 TextChanged 이벤트를 별도로 처리
            RegisterAudioViewTextBox(workTab?.TxtMovieFeaturePath, AudioViewFileCategory.Feature);
            RegisterAudioViewTextBox(workTab?.TxtNarrationKoPath, AudioViewFileCategory.NarrationKo);
            RegisterAudioViewTextBox(workTab?.TxtNarrationEnPath, AudioViewFileCategory.NarrationEn);
            RegisterAudioViewTextBox(workTab?.TxtSubtitleKoPath, AudioViewFileCategory.SubtitleKo);
            RegisterAudioViewTextBox(workTab?.TxtSubtitleEnPath, AudioViewFileCategory.SubtitleEn);
        }

        private void RegisterAudioViewTextBox(TextBox textBox, AudioViewFileCategory category)
        {
            if (textBox == null)
            {
                return;
            }

            if (!audioViewTextBoxMap.ContainsKey(textBox))
            {
                audioViewTextBoxMap[textBox] = category;
                textBox.TextChanged += AudioViewFileTextBox_TextChanged;
            }
        }

        private void AudioViewFileTextBox_TextChanged(object sender, EventArgs e)
        {
            if (sender is TextBox textBox && audioViewTextBoxMap.ContainsKey(textBox))
            {
                UpdateAudioViewFilesStatus();
            }
        }

        private bool TryGetAudioViewCategory(TextBox textBox, out AudioViewFileCategory category)
        {
            if (textBox != null && audioViewTextBoxMap.TryGetValue(textBox, out category))
            {
                return true;
            }

            category = default(AudioViewFileCategory);
            return false;
        }

        private AudioViewFileCategory ResolveCategoryOrDefault(TextBox textBox, AudioViewFileCategory fallback)
        {
            AudioViewFileCategory category;
            if (TryGetAudioViewCategory(textBox, out category))
            {
                return category;
            }

            return fallback;
        }

        private void InitializeAudioViewFilesPanel()
        {
            if (lvAudioViewFiles == null)
            {
                return;
            }

            lvAudioViewFiles.Items.Clear();
            audioViewFileItems.Clear();

            foreach (AudioViewFileCategory category in Enum.GetValues(typeof(AudioViewFileCategory)))
            {
                var item = new ListViewItem(GetAudioViewFileDisplayName(category));
                item.SubItems.Add("-");          // 로컬 파일
                item.SubItems.Add("미등록");     // 일치 여부
                item.SubItems.Add("");           // 등록 버튼 컬럼
                audioViewFileItems[category] = item;
                lvAudioViewFiles.Items.Add(item);
            }
            
            // ListView 이벤트 핸들러는 여러 번 중복 등록되지 않도록 먼저 제거 후 다시 등록한다.
            lvAudioViewFiles.MouseClick -= LvAudioViewFiles_MouseClick;
            lvAudioViewFiles.DrawColumnHeader -= lvAudioViewFiles_DrawColumnHeader;
            lvAudioViewFiles.DrawItem -= lvAudioViewFiles_DrawItem;
            lvAudioViewFiles.DrawSubItem -= lvAudioViewFiles_DrawSubItem;
            lvAudioViewFiles.SizeChanged -= LvAudioViewFiles_SizeChanged;

            // ListView 클릭 이벤트 핸들러 등록
            lvAudioViewFiles.MouseClick += LvAudioViewFiles_MouseClick;

            // ListView OwnerDraw 설정 및 이벤트 핸들러 연결
            lvAudioViewFiles.OwnerDraw = true;
            lvAudioViewFiles.DrawColumnHeader += lvAudioViewFiles_DrawColumnHeader;
            lvAudioViewFiles.DrawItem += lvAudioViewFiles_DrawItem;
            lvAudioViewFiles.DrawSubItem += lvAudioViewFiles_DrawSubItem;

            // 컨트롤 크기 변경 시 로컬/서버 컬럼 폭을 자동으로 재조정
            lvAudioViewFiles.SizeChanged += LvAudioViewFiles_SizeChanged;

            // 초기 컬럼 폭 정렬
            AdjustAudioViewFilesColumns();

            UpdateAudioViewFilesStatus();
        }

        /// <summary>
        /// lvAudioViewFiles의 컬럼 폭을 조정한다.
        /// - 항목(colFileType), 일치 여부(colStatus), 등록(colRegister) 컬럼은 고정 폭 유지
        /// - 로컬 파일(colLocalFile) 컬럼이 나머지 폭을 차지하도록 조정
        /// </summary>
        private void AdjustAudioViewFilesColumns()
        {
            if (lvAudioViewFiles == null ||
                colFileType == null || colLocalFile == null ||
                colStatus == null || colRegister == null)
            {
                return;
            }

            int clientWidth = lvAudioViewFiles.ClientSize.Width;
            if (clientWidth <= 0)
            {
                return;
            }

            // 고정 컬럼 폭 합산 (항목 + 일치 여부 + 등록)
            int fixedWidth = colFileType.Width + colStatus.Width + colRegister.Width;

            // 여유 마진(그리드/스크롤바 공간) 약간 확보
            int margin = 8;
            int remaining = clientWidth - fixedWidth - margin;
            if (remaining <= 0)
            {
                return;
            }

            // 남은 폭을 모두 로컬 파일(colLocalFile)에 할당
            int localWidth = remaining;

            if (localWidth < 50) localWidth = 50;

            colLocalFile.Width = localWidth;
        }

        private void LvAudioViewFiles_SizeChanged(object sender, EventArgs e)
        {
            AdjustAudioViewFilesColumns();
        }

        private string GetAudioViewFileDisplayName(AudioViewFileCategory category)
        {
            switch (category)
            {
                case AudioViewFileCategory.MovieAudioOrg:
                    return "원본 음원";
                case AudioViewFileCategory.MovieAudioWave:
                    return "영화 음원";
                case AudioViewFileCategory.Feature:
                    return "특징 파일";
                case AudioViewFileCategory.NarrationKo:
                    return "해설 음원 (한국어)";
                case AudioViewFileCategory.NarrationEn:
                    return "해설 음원 (영어)";
                case AudioViewFileCategory.SubtitleKo:
                    return "영화 자막 (한국어)";
                case AudioViewFileCategory.SubtitleEn:
                    return "영화 자막 (영어)";
                default:
                    return category.ToString();
            }
        }

        private void UpdateAudioViewFilesStatus()
        {
            // 서버에 연결되어 있고 서버 영화 ID가 있으면 서버에서 최신 체크섬 가져오기
            // 단, 이미 갱신 중이면 재귀 호출을 피한다.
            if (serverConnected && currentServerMovieId.HasValue && !isRefreshingChecksums)
            {
                _ = RefreshServerChecksumsAsync(currentServerMovieId.Value);
            }

            RefreshAudioViewFilesUI();
        }

        /// <summary>
        /// 서버에서 최신 체크섬 정보를 가져와서 로컬에 저장한다.
        /// </summary>
        private async Task RefreshServerChecksumsAsync(int serverMovieId)
        {
            if (!serverConnected || !ServerUrl.CheckServerConnectionUrl(dbInfo) || isRefreshingChecksums)
            {
                return;
            }

            isRefreshingChecksums = true;
            try
            {
                string apiUrl = ServerUrl.GetMovieAssetsUrl(dbInfo, serverMovieId);
                int timeoutSec = ServerUrl.GetRequestTimeoutSeconds(dbInfo);

                // 진행 상황을 서버 메시지 영역에 표시
                UpdateServerStatusLabel($"[체크섬] 서버 자산 목록 조회 시작 (영화 ID: {serverMovieId})", Color.Blue);

                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(timeoutSec);

                    var response = await httpClient.GetAsync(apiUrl);
                    if (!response.IsSuccessStatusCode)
                    {
                        UpdateServerStatusLabel($"[체크섬] 자산 목록 조회 실패: {(int)response.StatusCode} {response.ReasonPhrase}", Color.Orange);
                        return;
                    }

                    string json = await response.Content.ReadAsStringAsync();
                    MovieAssetListResponse assetList = null;
                    try
                    {
                        var serializer = new JavaScriptSerializer();
                        assetList = serializer.Deserialize<MovieAssetListResponse>(json);
                    }
                    catch
                    {
                        UpdateServerStatusLabel($"[체크섬] 자산 목록 응답 파싱 실패", Color.Orange);
                        return;
                    }

                    if (assetList?.items == null)
                    {
                        UpdateServerStatusLabel($"[체크섬] 자산 정보 없음 (items=null)", Color.Orange);
                        return;
                    }

                    UpdateServerStatusLabel($"[체크섬] 서버로부터 자산 {assetList.items.Count}개 정보 수신", Color.Blue);

                    // 서버에서 받은 체크섬을 로컬에 저장 (UI 스레드에서 실행)
                    if (this.InvokeRequired)
                    {
                        this.Invoke((MethodInvoker)delegate
                        {
                            foreach (var asset in assetList.items)
                            {
                                if (string.IsNullOrWhiteSpace(asset.type) || string.IsNullOrWhiteSpace(asset.checksum_sha256))
                                {
                                    continue;
                                }

                                AudioViewFileCategory? category = ConvertAssetTypeToCategory(asset.type);
                                if (category.HasValue)
                                {
                                    SetServerFileChecksum(category.Value, asset.checksum_sha256);
                                }
                            }
                            
                            // 체크섬만 업데이트하고 UI는 별도로 업데이트 (재귀 호출 방지)
                            RefreshAudioViewFilesUI();

                            UpdateServerStatusLabel("[체크섬] 서버 자산 체크섬 갱신 완료 (UI 반영)", Color.Green);
                        });
                    }
                    else
                    {
                        foreach (var asset in assetList.items)
                        {
                            if (string.IsNullOrWhiteSpace(asset.type) || string.IsNullOrWhiteSpace(asset.checksum_sha256))
                            {
                                continue;
                            }

                            AudioViewFileCategory? category = ConvertAssetTypeToCategory(asset.type);
                            if (category.HasValue)
                            {
                                SetServerFileChecksum(category.Value, asset.checksum_sha256);
                            }
                        }
                        
                        // 체크섬만 업데이트하고 UI는 별도로 업데이트 (재귀 호출 방지)
                        RefreshAudioViewFilesUI();

                        UpdateServerStatusLabel("[체크섬] 서버 자산 체크섬 갱신 완료 (UI 반영)", Color.Green);
                    }
                }
            }
            catch
            {
                // 오류 발생 시 무시 (백그라운드 작업이므로)
                UpdateServerStatusLabel("[체크섬] 서버 자산 체크섬 갱신 중 오류 발생 (자세한 내용은 로그 참조)", Color.Orange);
            }
            finally
            {
                isRefreshingChecksums = false;
            }
        }

        /// <summary>
        /// AudioViewFiles UI만 갱신 (체크섬 갱신 없이)
        /// </summary>
        private void RefreshAudioViewFilesUI()
        {
            if (audioViewFileItems.Count == 0)
            {
                return;
            }

            foreach (var kvp in audioViewFileItems)
            {
                var category = kvp.Key;
                var item = kvp.Value;

                string localPath = ResolveLocalAViewPath(category);
                string serverPath = ResolveServerAudioViewPath(category);

                string localDisplay = FormatAudioViewCell(localPath);

                if (item.SubItems.Count < 4)
                {
                    while (item.SubItems.Count < 4)
                    {
                        item.SubItems.Add(string.Empty);
                    }
                }

                item.SubItems[1].Text = localDisplay;

                string statusText;
                Color statusColor;
                bool showRegisterButton = false;

                // 서버에 연결되지 않은 경우: 항상 "로컬"로 표시 (서버 기준 정보 사용 불가)
                if (!serverConnected || !currentServerMovieId.HasValue)
                {
                    if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
                    {
                        statusText = "로컬 없음";
                        statusColor = Color.OrangeRed;
                    }
                    else
                    {
                        statusText = "로컬";
                        statusColor = Color.DimGray;
                        // 서버에 연결되면 등록할 수 있음을 표시하기 위해 등록 버튼 활성화
                        showRegisterButton = false; // 서버가 꺼져 있어도 버튼은 감춤
                    }
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(serverPath))
                    {
                        statusText = "미등록";
                        statusColor = Color.DimGray;
                        if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
                        {
                            showRegisterButton = true;
                        }
                    }
                    else if (string.IsNullOrWhiteSpace(localPath))
                    {
                        statusText = "로컬 없음";
                        statusColor = Color.OrangeRed;
                    }
                    else if (!File.Exists(localPath) || !File.Exists(serverPath))
                    {
                        statusText = "파일 누락";
                        statusColor = Color.DarkOrange;
                        if (File.Exists(localPath))
                        {
                            showRegisterButton = true;
                        }
                    }
                    else
                    {
                        // 체크섬 비교로 실제 파일 동일 여부 확인
                        string serverChecksum = GetServerFileChecksum(category);
                        string localChecksum = CalculateFileChecksum(localPath);
                        
                        if (!string.IsNullOrWhiteSpace(serverChecksum) && !string.IsNullOrWhiteSpace(localChecksum))
                        {
                            if (string.Equals(serverChecksum, localChecksum, StringComparison.OrdinalIgnoreCase))
                            {
                                statusText = "동기화됨";
                                statusColor = Color.ForestGreen;
                            }
                            else
                            {
                                statusText = "불일치";
                                statusColor = Color.Orange;
                                showRegisterButton = true;
                            }
                        }
                        else
                        {
                            // 체크섬 정보가 없으면 파일 경로 비교
                            if (IsSameFile(localPath, serverPath))
                            {
                                statusText = "일치";
                                statusColor = Color.ForestGreen;
                            }
                            else
                            {
                                statusText = "불일치";
                                statusColor = Color.Firebrick;
                                showRegisterButton = true;
                            }
                        }
                    }
                }

                item.SubItems[2].Text = statusText;
                item.SubItems[2].ForeColor = statusColor;
                
                if (showRegisterButton)
                {
                    item.SubItems[3].Text = "등록";
                    item.SubItems[3].ForeColor = Color.Blue;
                }
                else
                {
                    item.SubItems[3].Text = "";
                    item.SubItems[3].ForeColor = Color.Black;
                }
                
                item.ToolTipText = BuildAudioViewTooltip(localPath, serverPath);
            }

            // 영화 이미지 동기화 상태를 버튼 텍스트로 표시
            try
            {
                if (btnPickMovieImage != null)
                {
                    string imageStatus = "이미지 없음";

                    if (!string.IsNullOrWhiteSpace(currentMovieImagePath) && File.Exists(currentMovieImagePath))
                    {
                        if (!serverConnected || !currentServerMovieId.HasValue)
                        {
                            imageStatus = "이미지(로컬)";
                        }
                        else if (!string.IsNullOrWhiteSpace(serverImageChecksum))
                        {
                            string currentImageChecksum = CalculateFileChecksum(currentMovieImagePath);
                            if (!string.IsNullOrWhiteSpace(currentImageChecksum) &&
                                string.Equals(currentImageChecksum, serverImageChecksum, StringComparison.OrdinalIgnoreCase))
                            {
                                imageStatus = "이미지(동기화됨)";
                            }
                            else
                            {
                                imageStatus = "이미지(불일치)";
                            }
                        }
                        else
                        {
                            imageStatus = "이미지(로컬)";
                        }
                    }

                    btnPickMovieImage.Text = imageStatus;
                }
            }
            catch
            {
                // 이미지 상태 표시 중 오류는 무시 (UI만 영향)
            }
        }

        private void LvAudioViewFiles_MouseClick(object sender, MouseEventArgs e)
        {
            if (lvAudioViewFiles == null)
            {
                return;
            }

            ListViewHitTestInfo hitTest = lvAudioViewFiles.HitTest(e.Location);
            if (hitTest.Item == null)
            {
                return;
            }

            // 클릭한 컬럼 인덱스 확인
            int x = e.X;
            int columnIndex = -1;
            int accumulatedWidth = 0;
            
            for (int i = 0; i < lvAudioViewFiles.Columns.Count; i++)
            {
                accumulatedWidth += lvAudioViewFiles.Columns[i].Width;
                if (x <= accumulatedWidth)
                {
                    columnIndex = i;
                    break;
                }
            }

            // 등록 컬럼(인덱스 3)을 클릭했는지 확인
            if (columnIndex == 3)
            {
                // 등록 버튼이 있는지 확인
                if (hitTest.Item.SubItems.Count > 3 && 
                    !string.IsNullOrWhiteSpace(hitTest.Item.SubItems[3].Text) && 
                    hitTest.Item.SubItems[3].Text == "등록")
                {
                    // 해당 항목의 카테고리 찾기
                    AudioViewFileCategory? category = null;
                    foreach (var kvp in audioViewFileItems)
                    {
                        if (kvp.Value == hitTest.Item)
                        {
                            category = kvp.Key;
                            break;
                        }
                    }

                    if (category.HasValue)
                    {
                        RegisterAViewFileToServer(category.Value);
                    }
                }
            }
        }

        private async void RegisterAViewFileToServer(AudioViewFileCategory category)
        {
            string localPath = ResolveLocalAViewPath(category);
            
            if (string.IsNullOrWhiteSpace(localPath))
            {
                MessageBox.Show("등록할 로컬 파일이 없습니다.", "등록 불가", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!File.Exists(localPath))
            {
                MessageBox.Show("로컬 파일을 찾을 수 없습니다.", "파일 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var currentMovie = movieData.GetCurrentMovie();
            if (currentMovie == null)
            {
                MessageBox.Show("먼저 영화 정보를 선택하거나 생성해주세요.", "영화 정보 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 서버 영화 ID 확인
            string storedServerId = movieData.GetExtraAttribute(currentMovie.Id, ExtraKeyServerMovieId);
            if (string.IsNullOrWhiteSpace(storedServerId) || !int.TryParse(storedServerId, out int serverMovieId) || serverMovieId <= 0)
            {
                MessageBox.Show("서버에 영화 정보가 등록되지 않았습니다.\n먼저 영화 정보를 서버에 저장해주세요.", "영화 정보 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // 서버에 파일 업로드
                await UploadFileToServer(serverMovieId, category, localPath);
                
                // 로컬에도 등록 정보 저장
                SetServerRegisteredFile(category, localPath);
                
                // 파일 체크섬 저장
                string checksum = CalculateFileChecksum(localPath);
                if (!string.IsNullOrWhiteSpace(checksum))
                {
                    SetServerFileChecksum(category, checksum);
                }
                
                UpdateAudioViewFilesStatus();
                
                string displayName = GetAudioViewFileDisplayName(category);
                MessageBox.Show($"{displayName} 파일이 서버에 등록되었습니다.", "등록 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"서버 등록 중 오류가 발생했습니다.\n{ex.Message}", "등록 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string GetServerAssetType(AudioViewFileCategory category)
        {
            switch (category)
            {
                case AudioViewFileCategory.MovieAudioOrg:
                    return "audio_original";
                case AudioViewFileCategory.MovieAudioWave:
                    return "audio_wave";
                case AudioViewFileCategory.Feature:
                    return "audio_feature";
                case AudioViewFileCategory.NarrationKo:
                    return "audio_desc_ko";
                case AudioViewFileCategory.NarrationEn:
                    return "audio_desc_en";
                case AudioViewFileCategory.SubtitleKo:
                    return "subtitle_ko";
                case AudioViewFileCategory.SubtitleEn:
                    return "subtitle_en";
                default:
                    throw new ArgumentException($"지원하지 않는 파일 카테고리: {category}");
            }
        }

        private AudioViewFileCategory? ConvertAssetTypeToCategory(string assetType)
        {
            switch (assetType?.ToLowerInvariant())
            {
                case "audio_original":
                    return AudioViewFileCategory.MovieAudioOrg;
                case "audio_wave":
                    return AudioViewFileCategory.MovieAudioWave;
                case "audio_feature":
                    return AudioViewFileCategory.Feature;
                case "audio_desc_ko":
                    return AudioViewFileCategory.NarrationKo;
                case "audio_desc_en":
                    return AudioViewFileCategory.NarrationEn;
                case "subtitle_ko":
                    return AudioViewFileCategory.SubtitleKo;
                case "subtitle_en":
                    return AudioViewFileCategory.SubtitleEn;
                default:
                    return null;
            }
        }

        /// <summary>
        /// 작업 폴더에서 파일을 찾아서 lvAudioViewFiles의 각 카테고리별 로컬 파일로 자동 매핑
        /// </summary>
        private void MapFilesFromWorkFolderToAudioViewFiles(string movieId)
        {
            if (string.IsNullOrWhiteSpace(movieId))
            {
                return;
            }

            string movieFolderPath = movieData.GetMovieFolderPath(movieId);
            if (!Directory.Exists(movieFolderPath))
            {
                return;
            }

            // 작업 폴더 및 하위 폴더에서 파일 검색
            var allFiles = new List<string>();
            try
            {
                // 루트 폴더의 파일
                allFiles.AddRange(Directory.GetFiles(movieFolderPath, "*.*", SearchOption.TopDirectoryOnly));
                
                // 하위 폴더의 파일도 검색
                var subDirectories = Directory.GetDirectories(movieFolderPath);
                foreach (var subDir in subDirectories)
                {
                    allFiles.AddRange(Directory.GetFiles(subDir, "*.*", SearchOption.TopDirectoryOnly));
                }
            }
            catch
            {
                // 파일 검색 실패 시 무시
                return;
            }

            // 각 카테고리별로 파일 매핑
            var categoryFiles = new Dictionary<AudioViewFileCategory, List<string>>();

            foreach (var filePath in allFiles)
            {
                string fileName = Path.GetFileName(filePath).ToLowerInvariant();
                string extension = Path.GetExtension(fileName).ToLowerInvariant();

                // MovieAudio: 오디오 파일
                if (IsSupportedAudioFile(filePath))
                {
                    // 해설 음원이 아닌 경우에만 MovieAudio로 매핑
                    if (!fileName.Contains("narration") && !fileName.Contains("해설") && 
                        !fileName.Contains("desc") && !fileName.Contains("description"))
                    {
                        if (!categoryFiles.ContainsKey(AudioViewFileCategory.MovieAudioWave))
                        {
                            categoryFiles[AudioViewFileCategory.MovieAudioWave] = new List<string>();
                        }
                        categoryFiles[AudioViewFileCategory.MovieAudioWave].Add(filePath);
                    }
                }

                // Feature: 특징 파일 (CSV 등)
                if (extension == ".csv" || fileName.Contains("feature") || fileName.Contains("특징"))
                {
                    if (!categoryFiles.ContainsKey(AudioViewFileCategory.Feature))
                    {
                        categoryFiles[AudioViewFileCategory.Feature] = new List<string>();
                    }
                    categoryFiles[AudioViewFileCategory.Feature].Add(filePath);
                }

                // NarrationKo: 한국어 해설 음원
                if (IsSupportedAudioFile(filePath) && 
                    (fileName.Contains("ko") || fileName.Contains("korean") || 
                     fileName.Contains("한국어") || fileName.Contains("narration_ko") ||
                     fileName.Contains("desc_ko") || fileName.Contains("해설_ko")))
                {
                    if (!categoryFiles.ContainsKey(AudioViewFileCategory.NarrationKo))
                    {
                        categoryFiles[AudioViewFileCategory.NarrationKo] = new List<string>();
                    }
                    categoryFiles[AudioViewFileCategory.NarrationKo].Add(filePath);
                }

                // NarrationEn: 영어 해설 음원
                if (IsSupportedAudioFile(filePath) && 
                    (fileName.Contains("en") || fileName.Contains("english") || 
                     fileName.Contains("영어") || fileName.Contains("narration_en") ||
                     fileName.Contains("desc_en") || fileName.Contains("해설_en")))
                {
                    if (!categoryFiles.ContainsKey(AudioViewFileCategory.NarrationEn))
                    {
                        categoryFiles[AudioViewFileCategory.NarrationEn] = new List<string>();
                    }
                    categoryFiles[AudioViewFileCategory.NarrationEn].Add(filePath);
                }

                // SubtitleKo: 한국어 자막
                if ((extension == ".srt" || extension == ".vtt" || extension == ".ass") &&
                    (fileName.Contains("ko") || fileName.Contains("korean") || 
                     fileName.Contains("한국어") || fileName.Contains("subtitle_ko") ||
                     !fileName.Contains("en") && !fileName.Contains("english")))
                {
                    if (!categoryFiles.ContainsKey(AudioViewFileCategory.SubtitleKo))
                    {
                        categoryFiles[AudioViewFileCategory.SubtitleKo] = new List<string>();
                    }
                    categoryFiles[AudioViewFileCategory.SubtitleKo].Add(filePath);
                }

                // SubtitleEn: 영어 자막
                if ((extension == ".srt" || extension == ".vtt" || extension == ".ass") &&
                    (fileName.Contains("en") || fileName.Contains("english") || 
                     fileName.Contains("영어") || fileName.Contains("subtitle_en")))
                {
                    if (!categoryFiles.ContainsKey(AudioViewFileCategory.SubtitleEn))
                    {
                        categoryFiles[AudioViewFileCategory.SubtitleEn] = new List<string>();
                    }
                    categoryFiles[AudioViewFileCategory.SubtitleEn].Add(filePath);
                }
            }

            // 각 카테고리에 파일이 하나만 있으면 자동으로 설정
            // 여러 개가 있으면 첫 번째 파일을 설정
            foreach (var kvp in categoryFiles)
            {
                var category = kvp.Key;
                var files = kvp.Value;

                if (files.Count > 0)
                {
                    // 이미 설정된 파일이 없을 때만 자동 설정
                    string currentPath = ResolveLocalAViewPath(category);
                    if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
                    {
                        string fileToSet = files[0]; // 첫 번째 파일 사용
                        
                        // TextBox에 직접 파일 경로 설정
                        SetAViewFileToTextBox(category, fileToSet);

                        // 로컬 파일 변경 이벤트 호출
                        OnAViewLocalFileChanged(category, fileToSet, registerOnServer: false);
                    }
                }
            }
        }

        /// <summary>
        /// 모든 AudioView 관련 TextBox 초기화
        /// </summary>
        private void ClearAudioViewFileTextBoxes()
        {
            // TextBox 초기화 시 이벤트 발생을 막기 위해 suppress 플래그 사용
            bool wasSuppressing = suppressMovieFolderTextChange;
            suppressMovieFolderTextChange = true;
            
            try
            {
                if (workTab?.TxtMovieFile != null)
                {
                    workTab.TxtMovieFile.Text = string.Empty;
                    // Label은 Tag 속성이 없으므로 제거
                }
                if (workTab?.TxtMovieFeaturePath != null)
                {
                    InitializeSetPathTextBox(workTab.TxtMovieFeaturePath, string.Empty);
                }
                if (workTab?.TxtNarrationKoPath != null)
                {
                    InitializeSetPathTextBox(workTab.TxtNarrationKoPath, string.Empty);
                }
                if (workTab?.TxtNarrationEnPath != null)
                {
                    InitializeSetPathTextBox(workTab.TxtNarrationEnPath, string.Empty);
                }
                if (workTab?.TxtSubtitleKoPath != null)
                {
                    InitializeSetPathTextBox(workTab.TxtSubtitleKoPath, string.Empty);
                }
                if (workTab?.TxtSubtitleEnPath != null)
                {
                    InitializeSetPathTextBox(workTab.TxtSubtitleEnPath, string.Empty);
                }
            }
            finally
            {
                suppressMovieFolderTextChange = wasSuppressing;
            }
        }

        /// <summary>
        /// AudioViewFileCategory에 해당하는 TextBox에 파일 경로 설정
        /// </summary>
        private void SetAViewFileToTextBox(AudioViewFileCategory category, string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return;
            }

            switch (category)
            {
                case AudioViewFileCategory.MovieAudioOrg:
                    if (workTab?.TxtMovieFile != null)
                    {
                        // Label은 파일명만 표시 (Tag 속성 없음)
                        workTab.TxtMovieFile.Text = Path.GetFileName(filePath);
                    }
                    break;
                case AudioViewFileCategory.MovieAudioWave:
                    if (workTab?.LblWaveFilename != null)
                    {
                        // Label은 파일명만 표시 (Tag 속성 없음)
                        workTab.LblWaveFilename.Text = Path.GetFileName(filePath);
                    }
                    break;
                case AudioViewFileCategory.Feature:
                    if (workTab?.TxtMovieFeaturePath != null)
                    {
                        InitializeSetPathTextBox(workTab.TxtMovieFeaturePath, filePath);
                    }
                    break;
                case AudioViewFileCategory.NarrationKo:
                    if (workTab?.TxtNarrationKoPath != null)
                    {
                        InitializeSetPathTextBox(workTab.TxtNarrationKoPath, filePath);
                    }
                    break;
                case AudioViewFileCategory.NarrationEn:
                    if (workTab?.TxtNarrationEnPath != null)
                    {
                        InitializeSetPathTextBox(workTab.TxtNarrationEnPath, filePath);
                    }
                    break;
                case AudioViewFileCategory.SubtitleKo:
                    if (workTab?.TxtSubtitleKoPath != null)
                    {
                        InitializeSetPathTextBox(workTab.TxtSubtitleKoPath, filePath);
                    }
                    break;
                case AudioViewFileCategory.SubtitleEn:
                    if (workTab?.TxtSubtitleEnPath != null)
                    {
                        InitializeSetPathTextBox(workTab.TxtSubtitleEnPath, filePath);
                    }
                    break;
            }
        }


        /// <summary>
        /// 이미지를 서버에 업로드합니다.
        /// </summary>
        private async Task UploadImageToServer(int serverMovieId, string imagePath)
        {
            System.Diagnostics.Debug.WriteLine($"UploadImageToServer 호출됨 - serverMovieId: {serverMovieId}, imagePath: {imagePath}");
            
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                string errorMsg = $"이미지 파일이 없거나 경로가 잘못되었습니다. imagePath: {imagePath}, Exists: {File.Exists(imagePath)}";
                System.Diagnostics.Debug.WriteLine(errorMsg);
                throw new Exception(errorMsg);
            }

            string apiUrl = ServerUrl.GetMovieImageUrl(dbInfo, serverMovieId);
            int timeoutSec = ServerUrl.GetRequestTimeoutSeconds(dbInfo);
            
            System.Diagnostics.Debug.WriteLine($"이미지 업로드 URL: {apiUrl}");

            using (var httpClient = new HttpClient())
            {
                httpClient.Timeout = TimeSpan.FromSeconds(timeoutSec);

                try
                {
                    // 파일을 읽어서 multipart/form-data로 전송
                    using (var formData = new MultipartFormDataContent())
                    {
                        byte[] fileBytes = File.ReadAllBytes(imagePath);
                        string fileName = Path.GetFileName(imagePath);
                        var fileContent = new ByteArrayContent(fileBytes);
                        
                        // 이미지 파일의 MIME 타입 설정
                        string extension = Path.GetExtension(imagePath).ToLower();
                        string contentType = "image/jpeg";
                        if (extension == ".png")
                            contentType = "image/png";
                        else if (extension == ".gif")
                            contentType = "image/gif";
                        else if (extension == ".bmp")
                            contentType = "image/bmp";
                        
                        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
                        formData.Add(fileContent, "file", fileName);

                        UpdateServerStatusLabel($"이미지 업로드 중: {fileName}...", Color.Blue);
                        System.Diagnostics.Debug.WriteLine($"이미지 업로드 요청 전송: {fileName}, 크기: {fileBytes.Length} bytes");
                        
                        var response = await httpClient.PostAsync(apiUrl, formData);
                        string responseContent = await response.Content.ReadAsStringAsync();
                        
                        System.Diagnostics.Debug.WriteLine($"이미지 업로드 응답: StatusCode={response.StatusCode}, Content={responseContent}");

                        if (!response.IsSuccessStatusCode)
                        {
                            string errorMessage = $"이미지 업로드 실패: {(int)response.StatusCode} {response.ReasonPhrase}";
                            try
                            {
                                var serializer = new JavaScriptSerializer();
                                var errorObj = serializer.Deserialize<Dictionary<string, object>>(responseContent);
                                if (errorObj != null)
                                {
                                    if (errorObj.TryGetValue("message", out object msg))
                                    {
                                        errorMessage = msg?.ToString() ?? errorMessage;
                                    }
                                    if (errorObj.TryGetValue("code", out object code))
                                    {
                                        errorMessage += $"\n오류 코드: {code}";
                                    }
                                    if (errorObj.TryGetValue("detail", out object detail))
                                    {
                                        errorMessage += $"\n상세: {detail}";
                                    }
                                }
                            }
                            catch
                            {
                                if (!string.IsNullOrWhiteSpace(responseContent))
                                {
                                    errorMessage += $"\n응답 내용: {responseContent}";
                                }
                            }
                            System.Diagnostics.Debug.WriteLine($"이미지 업로드 실패: {errorMessage}");
                            throw new Exception(errorMessage);
                        }

                        System.Diagnostics.Debug.WriteLine($"이미지 업로드 성공: {responseContent}");
                        UpdateServerStatusLabel($"이미지 업로드 완료: {fileName}", Color.Green);
                    }
                }
                catch (Exception ex)
                {
                    UpdateServerStatusLabel($"이미지 업로드 오류: {ex.Message}", Color.Orange);
                    throw;
                }
            }
        }

        private async Task UploadFileToServer(int serverMovieId, AudioViewFileCategory category, string filePath)
        {
            string assetType = GetServerAssetType(category);
            string apiUrl = ServerUrl.GetMovieAssetUrl(dbInfo, serverMovieId, assetType);
            int timeoutSec = ServerUrl.GetRequestTimeoutSeconds(dbInfo);

            using (var httpClient = new HttpClient())
            {
                httpClient.Timeout = TimeSpan.FromSeconds(timeoutSec);

                try
                {
                    // 파일을 읽어서 multipart/form-data로 전송
                    using (var formData = new MultipartFormDataContent())
                    {
                        byte[] fileBytes = File.ReadAllBytes(filePath);
                        string fileName = Path.GetFileName(filePath);
                        var fileContent = new ByteArrayContent(fileBytes);
                        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                        formData.Add(fileContent, "file", fileName);

                        var response = await httpClient.PostAsync(apiUrl, formData);
                        string responseContent = await response.Content.ReadAsStringAsync();

                        if (!response.IsSuccessStatusCode)
                        {
                            string errorMessage = $"서버 응답 오류: {(int)response.StatusCode} {response.ReasonPhrase}";
                            try
                            {
                                var serializer = new JavaScriptSerializer();
                                var errorObj = serializer.Deserialize<Dictionary<string, object>>(responseContent);
                                if (errorObj != null)
                                {
                                    if (errorObj.TryGetValue("message", out object msg))
                                    {
                                        errorMessage = msg?.ToString() ?? errorMessage;
                                    }
                                    if (errorObj.TryGetValue("detail", out object detail))
                                    {
                                        errorMessage += $"\n상세: {detail}";
                                    }
                                }
                            }
                            catch
                            {
                                if (!string.IsNullOrWhiteSpace(responseContent))
                                {
                                    errorMessage += $"\n응답 내용: {responseContent}";
                                }
                            }

                            throw new Exception(errorMessage);
                        }
                    }
                }
                catch (TaskCanceledException ex)
                {
                    string errorMsg = $"서버 연결 시간 초과: {timeoutSec}초 내에 응답을 받지 못했습니다.\nURL: {apiUrl}";
                    throw new Exception(errorMsg, ex);
                }
                catch (HttpRequestException ex)
                {
                    string errorMsg = $"서버 연결 실패: {ex.Message}\n\n가능한 원인:\n1. 서버가 실행 중이지 않습니다\n2. 서버 주소나 포트가 올바르지 않습니다\n3. 방화벽이 연결을 차단하고 있습니다\n\nURL: {apiUrl}";
                    throw new Exception(errorMsg, ex);
                }
            }
        }

        private string FormatAudioViewCell(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "-";
            }

            try
            {
                return Path.GetFileName(path.Trim());
            }
            catch
            {
                return path;
            }
        }

        private string BuildAudioViewTooltip(string localPath, string serverPath)
        {
            var local = string.IsNullOrWhiteSpace(localPath) ? "없음" : localPath;
            var server = string.IsNullOrWhiteSpace(serverPath) ? "없음" : serverPath;
            return $"로컬: {local}{Environment.NewLine}서버: {server}";
        }

        private bool IsSameFile(string pathA, string pathB)
        {
            string normalizedA = NormalizePath(pathA);
            string normalizedB = NormalizePath(pathB);

            if (string.IsNullOrWhiteSpace(normalizedA) || string.IsNullOrWhiteSpace(normalizedB))
            {
                return false;
            }

            if (string.Equals(normalizedA, normalizedB, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            try
            {
                var infoA = new FileInfo(pathA);
                var infoB = new FileInfo(pathB);
                if (!infoA.Exists || !infoB.Exists)
                {
                    return false;
                }

                return infoA.Length == infoB.Length &&
                       infoA.LastWriteTimeUtc == infoB.LastWriteTimeUtc;
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch
            {
                return path.Trim();
            }
        }

        private string ResolveLocalAViewPath(AudioViewFileCategory category)
        {
            string overridePath;
            if (audioViewLocalPathOverrides.TryGetValue(category, out overridePath) &&
                !string.IsNullOrWhiteSpace(overridePath))
            {
                return overridePath;
            }

            switch (category)
            {
                case AudioViewFileCategory.MovieAudioOrg:
                    return workTab?.TxtMovieFile?.Text ?? string.Empty;
                case AudioViewFileCategory.MovieAudioWave:
                    return workTab?.LblWaveFilename?.Text ?? string.Empty;
                case AudioViewFileCategory.Feature:
                    return GetStoredFilePath(workTab?.TxtMovieFeaturePath);
                case AudioViewFileCategory.NarrationKo:
                    return GetStoredFilePath(workTab?.TxtNarrationKoPath);
                case AudioViewFileCategory.NarrationEn:
                    return GetStoredFilePath(workTab?.TxtNarrationEnPath);
                case AudioViewFileCategory.SubtitleKo:
                    return GetStoredFilePath(workTab?.TxtSubtitleKoPath);
                case AudioViewFileCategory.SubtitleEn:
                    return GetStoredFilePath(workTab?.TxtSubtitleEnPath);
                default:
                    return string.Empty;
            }
        }

        private string ResolveServerAudioViewPath(AudioViewFileCategory category)
        {
            // 이 함수는 서버가 연결되어 있어야 의미가 있음.
            if (!serverConnected) return string.Empty;

            var currentMovie = movieData.GetCurrentMovie();
            if (currentMovie == null)
            {
                return string.Empty;
            }

            string attributeKey = GetAudioViewAttributeKey(category);
            string storedRelative = movieData.GetExtraAttribute(currentMovie.Id, attributeKey);
            if (string.IsNullOrWhiteSpace(storedRelative))
            {
                return string.Empty;
            }

            if (Path.IsPathRooted(storedRelative))
            {
                return storedRelative;
            }

            string movieFolder = movieData.GetMovieFolderPath(currentMovie.Id);
            return Path.Combine(movieFolder, storedRelative);
        }

        private string GetAudioViewAttributeKey(AudioViewFileCategory category)
        {
            return $"AudioView.{category}";
        }

        private void OnAViewLocalFileChanged(AudioViewFileCategory category, string path, bool registerOnServer)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                if (audioViewLocalPathOverrides.ContainsKey(category))
                {
                    audioViewLocalPathOverrides.Remove(category);
                }
            }
            else
            {
                audioViewLocalPathOverrides[category] = path;
            }

            if (registerOnServer)
            {
                SetServerRegisteredFile(category, path);
            }

            UpdateAudioViewFilesStatus();
        }

        private void SetServerRegisteredFile(AudioViewFileCategory category, string path)
        {
            var currentMovie = movieData.GetCurrentMovie();
            if (currentMovie == null)
            {
                return;
            }

            string key = GetAudioViewAttributeKey(category);
            string value = string.Empty;

            if (!string.IsNullOrWhiteSpace(path))
            {
                string movieFolder = movieData.GetMovieFolderPath(currentMovie.Id);
                value = GetRelativePathSafe(movieFolder, path);
            }

            movieData.SetExtraAttribute(currentMovie.Id, key, value);
        }

        private static string GetRelativePathSafe(string basePath, string targetPath)
        {
            if (string.IsNullOrWhiteSpace(basePath) || string.IsNullOrWhiteSpace(targetPath))
            {
                return targetPath ?? string.Empty;
            }

            try
            {
                Uri baseUri = new Uri(AppendDirectorySeparatorChar(basePath));
                Uri targetUri = new Uri(targetPath);
                Uri relativeUri = baseUri.MakeRelativeUri(targetUri);
                string relativePath = Uri.UnescapeDataString(relativeUri.ToString());
                return relativePath.Replace('/', Path.DirectorySeparatorChar);
            }
            catch
            {
                return targetPath;
            }
        }

        private static string AppendDirectorySeparatorChar(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Path.DirectorySeparatorChar.ToString();
            }

            if (!path.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                return path + Path.DirectorySeparatorChar;
            }

            return path;
        }

        private void ResetAudioViewFileState()
        {
            audioViewLocalPathOverrides.Clear();
            UpdateAudioViewFilesStatus();
        }

        private void InitializeFeatureExtractionUI()
        {
            if (workTab == null)
            {
                return;
            }

            if (workTab.PrgFeatureExtract != null)
            {
                workTab.PrgFeatureExtract.Minimum = 0;
                workTab.PrgFeatureExtract.Maximum = 1000;
                workTab.PrgFeatureExtract.Value = 0;
                workTab.PrgFeatureExtract.Style = ProgressBarStyle.Continuous;
            }

            if (workTab.TxtFeatureLog != null)
            {
                workTab.TxtFeatureLog.Clear();
                workTab.TxtFeatureLog.WordWrap = false;
                workTab.TxtFeatureLog.ScrollBars = ScrollBars.Both;
            }

            if (workTab.PrgAudioConvertBar != null)
            {
                workTab.PrgAudioConvertBar.Minimum = 0;
                workTab.PrgAudioConvertBar.Maximum = 1000;
                workTab.PrgAudioConvertBar.Value = 0;
                workTab.PrgAudioConvertBar.Style = ProgressBarStyle.Continuous;
            }

            if (workTab.LblWaveFilename != null)
            {
                workTab.LblWaveFilename.Text = "-";
            }

            if (workTab.BtnCancelConvert != null)
            {
                workTab.BtnCancelConvert.Enabled = false;
            }

            ResetFeatureDisplay();
            RefreshFeatureAudioFiles();
            UpdateFeatureControlsState();
        }

        /// <summary>
        /// 핑거프린트 관련 콤보박스를 프로필 설정값으로 초기화합니다.
        /// </summary>
        private void InitializeFptComboBoxes(PickAudioFpParam param)
        {
            if (workTab == null)
            {
                return;
            }

            try
            {
                // MessagePack/JSON 라디오버튼 초기화
                ValidString vs = prof.GetItem(PDN.S_FINGERPRINT, PDN.E_CONVERSION, PDN.I_FILEFORM);
                if (vs.bValid && !string.IsNullOrEmpty(vs.sValue))
                {
                    if (vs.sValue.Equals(PDN.V_JSON))
                    {
                        workTab.rdoMPack.Checked = false;
                        workTab.rdoJson.Checked = true;
                    }
                    else
                    {
                        workTab.rdoMPack.Checked = true;
                        workTab.rdoJson.Checked = false;

                    }
                }
                else
                {   // 기본값은 MessagePack
                    workTab.rdoMPack.Checked = true;
                    workTab.rdoJson.Checked = false;
                }
                // Category 콤보박스 초기화
                workTab.cmbFptCvtCategory.SelectedIndex = param.mvCategoryId;

                // FFT Size 콤보박스 초기화
                workTab.cmbFptFFTSize.SelectedItem = param.fptCfg.FFTSize.ToString();

                // Hop Size 콤보박스 초기화
                workTab.CmbFptHopSize.SelectedItem = param.fptCfg.HopSize.ToString();

                vs = prof.GetItem(PDN.S_FINGERPRINT, PDN.E_CONVERSION, PDN.I_HASHONLY);
                //if (vs.bValid && !string.IsNullOrEmpty(vs.sValue))
                //{
                //    workTab.chkHashOnly.Checked = vs.sValue.Equals("on") ? true : false;
                //}
                //vs = prof.GetItem(PDN.S_FINGERPRINT, PDN.E_CONVERSION, PDN.I_REVERSEINDEX);
                //if (vs.bValid && !string.IsNullOrEmpty(vs.sValue))
                //{
                //    workTab.chkReverseIndex.Checked = vs.sValue.Equals("on") ? true : false;
                //}
            }
            catch (Exception ex)
            {
                // 초기화 실패 시 로그만 남기고 계속 진행
                System.Diagnostics.Debug.WriteLine($"핑거프린트 콤보박스 초기화 실패: {ex.Message}");
            }
        }

        private void RefreshFeatureAudioFiles(string preferredFileName = null)
        {
            if (workTab == null || workTab.CmbFeatureAudioFiles == null)
            {
                return;
            }

            suppressFeatureComboEvents = true;
            try
            {
                workTab.CmbFeatureAudioFiles.BeginUpdate();
                workTab.CmbFeatureAudioFiles.Items.Clear();

                var currentMovie = movieData.GetCurrentMovie();
                if (currentMovie == null)
                {
                    ResetFeatureDisplay();
                    UpdateFeatureControlsState();
                    return;
                }

                string movieFolder = movieData.GetMovieFolderPath(currentMovie.Id);
                if (!Directory.Exists(movieFolder))
                {
                    ResetFeatureDisplay();
                    UpdateFeatureControlsState();
                    return;
                }

                // 현재 로컬 폴더에서 직접 파일 목록을 스캔하여 콤보박스 항목을 구성한다.
                List<AudioFileComboItem> items = new List<AudioFileComboItem>();
                try
                {
                    var filePaths = Directory.EnumerateFiles(movieFolder, "*.*", SearchOption.TopDirectoryOnly)
                                             .Where(p => IsSupportedAudioFile(p))
                                             .ToList();

                    foreach (var path in filePaths)
                    {
                        var fileInfo = new FileInfo(path);
                        var workFile = new MovieData.MovieWorkFile
                        {
                            FileName = fileInfo.Name,
                            OriginalFileName = fileInfo.Name,
                            RelativePath = fileInfo.Name,
                            Description = null,
                            FileSize = fileInfo.Length,
                            ImportedAt = fileInfo.CreationTime,
                            UpdatedAt = fileInfo.LastWriteTime
                        };

                        var item = CreateAudioComboItem(currentMovie.Id, movieFolder, workFile);
                        if (item != null)
                        {
                            items.Add(item);
                        }
                    }
                }
                catch
                {
                    // 폴더 접근 오류 등은 빈 목록으로 처리
                }

                if (items.Count == 0)
                {
                    ResetFeatureDisplay();
                    UpdateFeatureControlsState();
                    return;
                }

                items.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
                foreach (var item in items)
                {
                    workTab.CmbFeatureAudioFiles.Items.Add(item);
                }

                int indexToSelect = 0;
                if (!string.IsNullOrWhiteSpace(preferredFileName))
                {
                    indexToSelect = items.FindIndex(item =>
                        string.Equals(item.FileName, preferredFileName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(item.OriginalFileName, preferredFileName, StringComparison.OrdinalIgnoreCase));
                    if (indexToSelect < 0)
                    {
                        indexToSelect = 0;
                    }
                }

                if (workTab.CmbFeatureAudioFiles.Items.Count > 0)
                {
                    workTab.CmbFeatureAudioFiles.SelectedIndex = Math.Max(0, Math.Min(indexToSelect, workTab.CmbFeatureAudioFiles.Items.Count - 1));
                    // ini 파일에 기록한다.
                    //string mvItem = workTab.CmbFeatureAudioFiles.SelectedItem.ToString();
                    //prof.WriteString(PDN.S_LATEST, PDN.E_MOVIEAUDIO, mvItem);
                }
            }
            finally
            {
                workTab.CmbFeatureAudioFiles.EndUpdate();
                suppressFeatureComboEvents = false;

                if (workTab.CmbFeatureAudioFiles.SelectedItem is AudioFileComboItem item)
                {
                    _ = UpdateSelectedAudioInfoAsync(item);
                }
                else
                {
                    ResetFeatureDisplay();
                }

                UpdateFeatureControlsState();
            }
        }

        private AudioFileComboItem CreateAudioComboItem(string movieId, string movieFolder, MovieData.MovieWorkFile file)
        {
            if (file == null)
            {
                return null;
            }

            string relativePath = !string.IsNullOrWhiteSpace(file.RelativePath) ? file.RelativePath : file.FileName;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            string fullPath = null;
            
            // 먼저 GetOrCreateMovieFolder()로 생성한 폴더에서 파일 찾기
            try
            {
                string createdFolderPath = GetOrCreateMovieFolder();
                string createdFolderFilePath = Path.Combine(createdFolderPath, relativePath);
                if (File.Exists(createdFolderFilePath))
                {
                    fullPath = createdFolderFilePath;
                }
            }
            catch
            {
                // GetOrCreateMovieFolder 실패 시 무시하고 기본 경로 사용
            }
            
            // 우리가 만든 폴더에 파일이 없으면 기본 movieFolder에서 찾기
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                fullPath = Path.Combine(movieFolder, relativePath);
            }
            
            if (!File.Exists(fullPath))
            {
                return null;
            }

            return new AudioFileComboItem(movieId, file, fullPath);
        }

        private void ResetFeatureDisplay()
        {
            if (workTab == null)
            {
                return;
            }

            if (workTab.PrgFeatureExtract != null)
            {
                workTab.PrgFeatureExtract.Value = 0;
            }

            lblFeatureStatus.Text = "상태: 대기 중";
            if (workTab.LblFeatureProgressPercent != null)
            {
                workTab.LblFeatureProgressPercent.Text = "진행: 0%";
            }
            if (workTab.LblFeatureElapsed != null)
            {
                workTab.LblFeatureElapsed.Text = "경과: 00:00:00";
            }
            if (workTab.LblFeatureRemaining != null)
            {
                workTab.LblFeatureRemaining.Text = "남은 시간: --";
            }
            if (workTab.LblFeatureTotalDuration != null)
            {
                workTab.LblFeatureTotalDuration.Text = "전체 재생시간: --";
            }
            if (workTab.LblSFPTimelineCurrent != null)
            {
                workTab.LblSFPTimelineCurrent.Text = "현재 진행: 00:00:00";
            }
            if (workTab.LblFeatureOutputPath != null)
            {
                workTab.LblFeatureOutputPath.Text = "출력 파일: -";
            }
            if (workTab.PrgAudioConvertBar != null)
            {
                workTab.PrgAudioConvertBar.Value = workTab.PrgAudioConvertBar.Minimum;
            }

            if (workTab.LblWaveFilename != null)
            {
                workTab.LblWaveFilename.Text = "-";
            }

            if (workTab.BtnCancelConvert != null && !audioConversionInProgress)
            {
                workTab.BtnCancelConvert.Enabled = false;
            }

            featureExtractionPaused = false;
        }

        private void UpdateFeatureControlsState()
        {
            if (workTab == null)
            {
                return;
            }

            bool isProcessing = featureExtractionCts != null;
            bool hasSelection = workTab.CmbFeatureAudioFiles?.SelectedItem is AudioFileComboItem;

            // BtnFptExtract 버튼 상태 업데이트 (Extract/Hold 전환)
            if (workTab.BtnFptExtract != null)
            {
                if (!isProcessing)
                {
                    // 진행 중이 아닐 때: "Extract" 표시
                    workTab.BtnFptExtract.Text = "Extract";
                    workTab.BtnFptExtract.Enabled = hasSelection;
                }
                else if (featureExtractionPaused)
                {
                    // 일시 중지 상태일 때: "Extract" 표시 (재개)
                    workTab.BtnFptExtract.Text = "Extract";
                    workTab.BtnFptExtract.Enabled = true;
                }
                else
                {
                    // 진행 중일 때: "Hold" 표시
                    workTab.BtnFptExtract.Text = "Hold";
                    workTab.BtnFptExtract.Enabled = true;
                }
            }
            if (workTab.BtnCancelFeatureExtract != null)
            {
                workTab.BtnCancelFeatureExtract.Enabled = isProcessing;
            }
            if (workTab.CmbFeatureAudioFiles != null)
            {
                workTab.CmbFeatureAudioFiles.Enabled = !isProcessing;
            }
            if (workTab.BtnRefreshFeatureAudioFiles != null)
            {
                workTab.BtnRefreshFeatureAudioFiles.Enabled = !isProcessing;
            }
            UpdateAudioConvertControlsState();
        }

        private void UpdateAudioConvertControlsState()
        {
            if (workTab == null || workTab.BtnConvertToWave == null)
            {
                return;
            }

            var selectedItem = TryGetSelectedAudioItem();
            bool isMp3 = IsMp3File(selectedItem);
            bool isFeatureProcessing = featureExtractionCts != null;
            workTab.BtnConvertToWave.Enabled = isMp3 && !audioConversionInProgress && !isFeatureProcessing;

            if (workTab.BtnCancelConvert != null)
            {
                bool canCancel = audioConversionInProgress && audioConversionCts != null && !audioConversionCts.IsCancellationRequested;
                workTab.BtnCancelConvert.Enabled = canCancel;
            }
        }

        private void AppendFeatureLog(string message)
        {
            if (workTab == null || workTab.TxtFeatureLog == null || workTab.TxtFeatureLog.IsDisposed)
            {
                return;
            }

            if (workTab.TxtFeatureLog.InvokeRequired)
            {
                workTab.TxtFeatureLog.BeginInvoke(new Action(() => AppendFeatureLog(message)));
                return;
            }

            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            if (workTab.TxtFeatureLog.TextLength > 0)
            {
                workTab.TxtFeatureLog.AppendText(Environment.NewLine);
            }
            workTab.TxtFeatureLog.AppendText(line);
            TrimFeatureLogLines();
            workTab.TxtFeatureLog.SelectionStart = workTab.TxtFeatureLog.TextLength;
            workTab.TxtFeatureLog.ScrollToCaret();
        }

        private void TrimFeatureLogLines(int maxLines = 500)
        {
            if (workTab == null || workTab.TxtFeatureLog == null)
            {
                return;
            }

            var lines = workTab.TxtFeatureLog.Lines;
            if (lines.Length <= maxLines)
            {
                return;
            }

            var trimmed = lines.Skip(Math.Max(0, lines.Length - maxLines)).ToArray();
            workTab.TxtFeatureLog.Lines = trimmed;
            workTab.TxtFeatureLog.SelectionStart = workTab.TxtFeatureLog.TextLength;
        }

        private static string FormatTimeSpan(TimeSpan value, bool includeMilliseconds = false)
        {
            string format = includeMilliseconds ? @"hh\:mm\:ss\.fff" : @"hh\:mm\:ss";
            if (value < TimeSpan.Zero)
            {
                value = TimeSpan.Zero;
            }

            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        private AudioFileComboItem TryGetSelectedAudioItem()
        {
            if (workTab == null || workTab.CmbFeatureAudioFiles == null)
            {
                return null;
            }
            return workTab.CmbFeatureAudioFiles.SelectedItem as AudioFileComboItem;
        }

        private static bool IsMp3File(AudioFileComboItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.FullPath))
            {
                return false;
            }

            string extension = Path.GetExtension(item.FullPath);
            return string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase);
        }

        private void ResetProgressForRun()
        {
            if (workTab == null)
            {
                return;
            }

            if (workTab.PrgFeatureExtract != null)
            {
                workTab.PrgFeatureExtract.Value = 0;
            }

            if (workTab.LblFeatureProgressPercent != null)
            {
                workTab.LblFeatureProgressPercent.Text = "진행: 0%";
            }
            if (workTab.LblFeatureElapsed != null)
            {
                workTab.LblFeatureElapsed.Text = "경과: 00:00:00";
            }
            if (workTab.LblFeatureRemaining != null)
            {
                workTab.LblFeatureRemaining.Text = "남은 시간: --";
            }
            lblFeatureStatus.Text = "상태: 준비 중...";
            if (workTab.LblSFPTimelineCurrent != null)
            {
                workTab.LblSFPTimelineCurrent.Text = "현재 진행: 00:00:00";
            }

            TimeSpan? totalDuration = currentAudioMetadata?.Duration;
            if (workTab?.LblFeatureTotalDuration != null)
            {
                if (totalDuration.HasValue)
                {
                    workTab.LblFeatureTotalDuration.Text = $"전체 재생시간: {FormatTimeSpan(totalDuration.Value)}";
                }
                else
                {
                    workTab.LblFeatureTotalDuration.Text = "전체 재생시간: --";
                }
            }

            if (workTab?.LblFeatureOutputPath != null)
            {
                workTab.LblFeatureOutputPath.Text = "출력 파일: -";
            }
        }

        private void UpdateFeatureProgress(AudioFeatures.ExtractionProgress progress)
        {
            // 취소가 요청된 경우 진행률 업데이트를 무시
            if (featureExtractionCts != null && featureExtractionCts.IsCancellationRequested)
            {
                return;
            }

            if (workTab == null)
            {
                return;
            }

            double percent = progress.PercentCompleted * 100.0;
            if (workTab.PrgFeatureExtract != null)
            {
                int value = (int)Math.Round(progress.PercentCompleted * workTab.PrgFeatureExtract.Maximum);
                value = Math.Max(workTab.PrgFeatureExtract.Minimum, Math.Min(workTab.PrgFeatureExtract.Maximum, value));
                workTab.PrgFeatureExtract.Value = value;
            }

            if (workTab.LblFeatureProgressPercent != null)
            {
                workTab.LblFeatureProgressPercent.Text = $"진행: {percent:0.0}%";
            }
            if (workTab.LblFeatureElapsed != null)
            {
                workTab.LblFeatureElapsed.Text = $"경과: {FormatTimeSpan(progress.Elapsed, includeMilliseconds: true)}";
            }

            if (workTab.LblFeatureRemaining != null)
            {
                if (progress.EstimatedRemaining.HasValue)
                {
                    workTab.LblFeatureRemaining.Text = $"남은 시간: {FormatTimeSpan(progress.EstimatedRemaining.Value, includeMilliseconds: true)}";
                }
                else
                {
                    workTab.LblFeatureRemaining.Text = "남은 시간: --";
                }
            }

            if (workTab.LblSFPTimelineCurrent != null)
            {
                workTab.LblSFPTimelineCurrent.Text = $"현재 진행: {FormatTimeSpan(progress.CurrentFrameStart)} ~ {FormatTimeSpan(progress.CurrentFrameEnd)}";
            }
            lblFeatureStatus.Text = $"상태: 진행 중 ({progress.FramesProcessed}/{progress.TotalFrames} 프레임)";

            if (workTab.LblFeatureTotalDuration != null && progress.TotalDuration.HasValue)
            {
                workTab.LblFeatureTotalDuration.Text = $"전체 재생시간: {FormatTimeSpan(progress.TotalDuration.Value)}";
            }

            if (percent >= 100.0)
            {
                lastLoggedProgress = 100.0;
            }
            else if (percent - lastLoggedProgress >= 5.0)
            {
                AppendFeatureLog($"진행 {percent:0.0}% (프레임 {progress.FramesProcessed}/{progress.TotalFrames})");
                lastLoggedProgress = percent;
            }
        }

        private async Task UpdateSelectedAudioInfoAsync(AudioFileComboItem item)
        {
            metadataCts?.Cancel();
            metadataCts?.Dispose();
            metadataCts = null;
            currentAudioMetadata = null;

            if (item == null)
            {
                ResetFeatureDisplay();
                return;
            }

            if (IsMp3File(item))
            {
                currentAudioMetadata = null;
                if (workTab?.LblSFPTimelineCurrent != null)
                {
                    workTab.LblSFPTimelineCurrent.Text = "현재 진행: 00:00:00";
                }
                if (workTab?.LblFeatureTotalDuration != null)
                {
                    workTab.LblFeatureTotalDuration.Text = "전체 재생시간: --";
                }
                if (workTab?.LblFeatureProgressPercent != null)
                {
                    workTab.LblFeatureProgressPercent.Text = "진행: 0%";
                }
                if (workTab?.LblFeatureElapsed != null)
                {
                    workTab.LblFeatureElapsed.Text = "경과: 00:00:00";
                }
                if (workTab?.LblFeatureRemaining != null)
                {
                    workTab.LblFeatureRemaining.Text = "남은 시간: --";
                }
                if (workTab?.LblFeatureOutputPath != null)
                {
                    workTab.LblFeatureOutputPath.Text = "출력 파일: -";
                }
                if (workTab?.LblWaveFilename != null)
                {
                    workTab.LblWaveFilename.Text = "변환 필요";
                }
                AppendFeatureLog($"MP3 파일이 선택되었습니다. WAV 변환 후 특징 추출을 진행할 수 있습니다. ({item.FileName})");
                UpdateAudioConvertControlsState();
                return;
            }

            if (!AudioFeatures.IsSupportedAudioFile(item.FullPath))
            {
                AppendFeatureLog("지원되지 않는 음원 형식입니다. WAV 파일만 지원됩니다.");
                MessageBox.Show("현재는 WAV 파일만 특징 추출을 지원합니다. WAV 파일을 선택해주세요.", "지원되지 않는 형식", MessageBoxButtons.OK, MessageBoxIcon.Information);
                currentAudioMetadata = null;
                ResetFeatureDisplay();
                return;
            }

            var cts = new CancellationTokenSource();
            metadataCts = cts;

            try
            {
                var metadata = await Task.Run(() => AudioFeatures.GetAudioMetadata(item.FullPath), cts.Token);
                if (cts.IsCancellationRequested)
                {
                    return;
                }

                currentAudioMetadata = metadata;
                if (workTab?.LblSFPTimelineCurrent != null)
                {
                    workTab.LblSFPTimelineCurrent.Text = "현재 진행: 00:00:00";
                }
                if (workTab?.LblFeatureTotalDuration != null)
                {
                    workTab.LblFeatureTotalDuration.Text = $"전체 재생시간: {FormatTimeSpan(metadata.Duration)}";
                }
                lblFeatureStatus.Text = $"상태: 대기 중 | 파일: {item.FileName}";
                if (workTab?.LblFeatureProgressPercent != null)
                {
                    workTab.LblFeatureProgressPercent.Text = "진행: 0%";
                }
                if (workTab?.LblFeatureElapsed != null)
                {
                    workTab.LblFeatureElapsed.Text = "경과: 00:00:00";
                }
                if (workTab?.LblFeatureRemaining != null)
                {
                    workTab.LblFeatureRemaining.Text = "남은 시간: --";
                }
                if (workTab?.LblFeatureOutputPath != null)
                {
                    workTab.LblFeatureOutputPath.Text = "출력 파일: -";
                }
            }
            catch (OperationCanceledException)
            {
                // 무시
            }
            catch (Exception ex)
            {
                AppendFeatureLog($"메타데이터 분석 실패: {ex.Message}");
                MessageBox.Show($"음원 정보를 읽는 중 오류가 발생했습니다.\n{ex.Message}", "메타데이터 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                currentAudioMetadata = null;
                ResetFeatureDisplay();
            }
            finally
            {
                if (metadataCts == cts)
                {
                    metadataCts.Dispose();
                    metadataCts = null;
                }

                UpdateFeatureControlsState();
                UpdateAudioConvertControlsState();
            }
        }

        private async Task StartFeatureExtractionAsync()
        {
            if (featureExtractionCts != null)
            {
                return;
            }

            var selectedItem = TryGetSelectedAudioItem();
            if (selectedItem == null)
            {
                MessageBox.Show("특징을 추출할 음원 파일을 선택해주세요.", "음원 선택 필요", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!File.Exists(selectedItem.FullPath))
            {
                MessageBox.Show("선택한 음원 파일을 찾을 수 없습니다. 파일이 이동되었는지 확인하세요.", "파일 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshFeatureAudioFiles();
                return;
            }

            // SFPFM 핑거프린트 추출은 FFT 크기와 Hop 크기를 사용하므로
            // 프레임 단위 시간(nudFeatureFrameDuration)은 사용하지 않음
            // (Live 특징 추출 등 다른 기능에서 사용됨)
            
            var currentMovie = movieData.GetCurrentMovie();
            if (currentMovie == null)
            {
                MessageBox.Show("먼저 영화 정보를 등록하거나 선택해주세요.", "영화 정보 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string movieFolder = movieData.GetMovieFolderPath(currentMovie.Id);
            string outputDirectory = Path.Combine(movieFolder, "features");
            Directory.CreateDirectory(outputDirectory);

            if (!AudioFeatures.IsSupportedAudioFile(selectedItem.FullPath))
            {
                MessageBox.Show("현재는 WAV 파일만 특징 추출을 지원합니다.", "지원되지 않는 형식", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            featureExtractionPauseToken?.Dispose();
            featureExtractionPauseToken = new AudioFeatures.PauseTokenSource();
            featureExtractionPaused = false;

            featureExtractionCts = new CancellationTokenSource();
            featureExtractionStartTime = DateTime.Now;
            lastLoggedProgress = 0;

            ResetProgressForRun();
            UpdateFeatureControlsState();
            AppendFeatureLog($"Original-PP 핑거프린트 추출 시작: {selectedItem.FileName}");

            // 출력 파일 경로 생성 (fingerprint 폴더에 저장)
            string fingerprintDirectory = Path.Combine(movieFolder, "fingerprint");
            Directory.CreateDirectory(fingerprintDirectory);
            string baseName = Path.GetFileNameWithoutExtension(selectedItem.FileName);
            string sanitizedBase = MovieData.SanitizeFileName(baseName);
            //var nameTail = workTab.FptFileInfo.MovieFptFNameTail();
            int FFTSize = workTab.pickParam.fptCfg.FFTSize;
            int HopSize = workTab.pickParam.fptCfg.HopSize;
            var nameTail = workTab.FptFileInfo.MovieFptFNameByValues(FFTSize, HopSize);
            string outputFilePath = Path.Combine(fingerprintDirectory, $"{sanitizedBase}" + nameTail);

            // 최종 파일이 이미 존재하는지 확인
            if (File.Exists(outputFilePath))
            {
                var fileInfo = new FileInfo(outputFilePath);
                string fileSize = fileInfo.Length > 1024 * 1024 
                    ? $"{fileInfo.Length / (1024.0 * 1024.0):F2} MB"
                    : $"{fileInfo.Length / 1024.0:F2} KB";
                string lastModified = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");

                DialogResult result = MessageBox.Show(
                    $"핑거프린트 파일이 이미 존재합니다.\n\n" + $"파일: {Path.GetFileName(outputFilePath)}\n" + $"크기: {fileSize}\n" +
                    $"수정일: {lastModified}\n\n" + $"다시 추출하시겠습니까?",
                    "핑거프린트 파일 존재", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if (result == DialogResult.No)
                {
                    AppendFeatureLog("핑거프린트 추출이 사용자에 의해 취소되었습니다. (기존 파일 유지)");
                    return;
                }

                // DialogResult.Yes인 경우: 기존 파일과 중간 파일을 삭제하여 영화음원에서 다시 생성하도록 함
                // (ExtractOriginalFPInternal이 기존 파일을 로딩하지 않도록)
                try
                {
                    // 최종 출력 파일 삭제
                    File.Delete(outputFilePath);
                    
                    // 중간 파일도 삭제 (peaks, fingerprints)
                    string directory = Path.GetDirectoryName(outputFilePath);
                    string fileName = Path.GetFileNameWithoutExtension(outputFilePath);
                    string extension = Path.GetExtension(outputFilePath);
                    
                    string peaksFilePath = Path.Combine(directory ?? "", $"{fileName}.peaks{extension}");
                    string fingerprintsFilePath = Path.Combine(directory ?? "", $"{fileName}.fingerprints{extension}");
                    
                    if (File.Exists(peaksFilePath))
                    {
                        File.Delete(peaksFilePath);
                    }
                    if (File.Exists(fingerprintsFilePath))
                    {
                        File.Delete(fingerprintsFilePath);
                    }
                    
                    AppendFeatureLog($"기존 핑거프린트 파일과 중간 파일을 삭제하고 영화음원에서 다시 생성합니다: {Path.GetFileName(outputFilePath)}");
                }
                catch (Exception ex)
                {
                    AppendFeatureLog($"기존 핑거프린트 파일 삭제 실패: {ex.Message}");
                    // 파일 삭제 실패 시에도 계속 진행 (덮어쓰기 시도)
                }
            }

            var progress = new Progress<OriginalFPProgress>(p =>
            {
                // 진행 상황 업데이트
                TimeSpan elapsed = DateTime.Now - featureExtractionStartTime;
                
                if (workTab?.LblFeatureProgressPercent != null)
                {
                    workTab.LblFeatureProgressPercent.Text = $"진행: {p.ProgressPercent:0.0}%";
                }
                if (workTab?.PrgFeatureExtract != null)
                {
                    workTab.PrgFeatureExtract.Value = Math.Min(1000, (int)(p.ProgressPercent * 10));
                }
                if (workTab?.LblSFPTimelineCurrent != null)
                {
                    // CurrentAction이 있으면 동작 표시, 없으면 시간 표시
                    if (!string.IsNullOrWhiteSpace(p.CurrentAction))
                    {
                        workTab.LblSFPTimelineCurrent.Text = p.CurrentAction;
                    }
                    else if (p.CurrentTime.TotalSeconds > 0)
                    {
                        workTab.LblSFPTimelineCurrent.Text = $"현재 진행: {FormatTimeSpan(p.CurrentTime)}";
                    }
                    else
                    {
                        workTab.LblSFPTimelineCurrent.Text = "준비 중...";
                    }
                }
                if (workTab?.LblFeatureElapsed != null)
                {
                    workTab.LblFeatureElapsed.Text = $"경과: {FormatTimeSpan(elapsed, includeMilliseconds: true)}";
                }
                if (workTab?.LblFeatureRemaining != null && p.TotalFrames > 0 && p.ProcessedFrames > 0)
                {
                    double remainingSeconds = (p.TotalFrames - p.ProcessedFrames) * (elapsed.TotalSeconds / p.ProcessedFrames);
                    workTab.LblFeatureRemaining.Text = $"남은 시간: {FormatTimeSpan(TimeSpan.FromSeconds(remainingSeconds))}";
                }
            });

            try
            {
                // FFT 크기와 Hop 크기는 이미 위에서 선언됨
                var result = await SFPFM.ExtractOriginalFPAsync(
                    workTab.pickParam,
                    selectedItem.FullPath,
                    outputFilePath,
                    progress,
                    featureExtractionCts.Token,
                    featureExtractionPauseToken,
                    statusMsgCbk: (message) =>
                    {
                        try
                        {
                            // 폼이 dispose되었거나 dispose 중이면 무시
                            if (IsDisposed || Disposing)
                            {
                                return;
                            }
                            
                            // Handle이 생성되지 않았으면 무시
                            if (!IsHandleCreated)
                            {
                                return;
                            }
                            
                            if (InvokeRequired)
                            {
                                // MainForm.Designer.cs:18 크래시 방지: BeginInvoke 전에 강력한 dispose 체크
                                // Thread.cs GetCurrentThreadNative() 크래시 방지: BeginInvoke 전에 메모리 정리
                                // BeginInvoke는 UI 스레드로 마샬링하는데, 이 과정에서 Thread.CurrentThread가 호출될 수 있음
                                try
                                {
                                    // BeginInvoke 전에 강력한 dispose 체크
                                    if (IsDisposed || Disposing || !IsHandleCreated)
                                    {
                                        return;
                                    }
                                    
                                    // Application.Run 크래시 방지: BeginInvoke 전 GC.Collect 제거 (UI 스레드 블로킹 방지)
                                    // GC.Collect가 UI 스레드를 블로킹하여 Application.Run 크래시 발생 가능
                                    
                                    // BeginInvoke 사용: 비동기 호출로 데드락 방지
                                    // MainForm.Designer.cs:18 크래시 방지: BeginInvoke 호출 자체를 try-catch로 감싸기
                                    BeginInvoke(new Action<string>((msg) =>
                                    {
                                        try
                                        {
                                            // 다시 한 번 상태 확인
                                            if (IsDisposed || Disposing || !IsHandleCreated) {return;}
                                            
                                            if (workTab?.LblSFPTimelineCurrent != null && !workTab.IsDisposed)
                                            {
                                                workTab.LblSFPTimelineCurrent.Text = msg;
                                            }
                                        }
                                        catch (ObjectDisposedException)
                                        {
                                            // 컨트롤이 dispose된 경우 무시
                                        }
                                        catch
                                        {
                                            // 기타 UI 업데이트 실패 시 무시
                                        }
                                    }), message);
                                }
                                catch (OutOfMemoryException)
                                {
                                    // Thread.cs GetCurrentThreadNative() 크래시 방지: 메모리 부족 시 BeginInvoke 건너뜀
                                    // BeginInvoke는 UI 스레드로 마샬링하는데, 이 과정에서 Thread.CurrentThread가 호출될 수 있음
                                }
                                catch (ObjectDisposedException)
                                {
                                    // MainForm.Designer.cs:18 크래시 방지: 폼이 dispose된 경우 무시
                                }
                                catch (InvalidOperationException)
                                {
                                    // Handle이 없거나 폼이 닫힌 경우 무시
                                }
                                catch
                                {
                                    // 기타 BeginInvoke 실패 시 무시
                                }
                            }
                            else
                            {
                                // UI 스레드에서 직접 호출
                                try
                                {
                                    if (!IsDisposed && !Disposing && workTab?.LblSFPTimelineCurrent != null && !workTab.IsDisposed)
                                    {
                                        workTab.LblSFPTimelineCurrent.Text = message;
                                    }
                                }
                                catch (OutOfMemoryException)
                                {
                                    // Thread.cs GetCurrentThreadNative() 크래시 방지: 메모리 부족 시 UI 업데이트 건너뜀
                                }
                                catch (ObjectDisposedException)
                                {
                                    // 컨트롤이 dispose된 경우 무시
                                }
                                catch
                                {
                                    // 기타 UI 업데이트 실패 시 무시
                                }
                            }
                        }
                        catch (OutOfMemoryException)
                        {
                            // Thread.cs GetCurrentThreadNative() 크래시 방지: 메모리 부족 시 statusMessageCallback 호출 건너뜀
                        }
                        catch (ObjectDisposedException)
                        {
                            // 폼이 dispose된 경우 무시
                        }
                        catch
                        {
                            // 기타 예외는 무시 (애플리케이션 종료 중일 수 있음)
                        }
                    });

                if (result.WasCanceled)
                {
                    AppendFeatureLog("Original-PP 핑거프린트 추출이 사용자에 의해 중단되었습니다.");
                    lblFeatureStatus.Text = "상태: 중단됨";
                    if (workTab?.LblFeatureProgressPercent != null)
                    {
                        workTab.LblFeatureProgressPercent.Text = "진행: 0%";
                    }
                    if (workTab?.LblFeatureRemaining != null)
                    {
                        workTab.LblFeatureRemaining.Text = "남은 시간: --";
                    }
                    if (workTab?.LblFeatureOutputPath != null)
                    {
                        workTab.LblFeatureOutputPath.Text = "출력 파일: -";
                    }
                    return;
                }

                lblFeatureStatus.Text = $"상태: 완료 (핑거프린트 {result.TotalFingerprints}개)";
                if (workTab?.LblFeatureProgressPercent != null)
                {
                    workTab.LblFeatureProgressPercent.Text = "진행: 100%";
                }
                if (workTab?.PrgFeatureExtract != null)
                {
                    workTab.PrgFeatureExtract.Value = 1000;
                }
                if (workTab?.LblFeatureElapsed != null)
                {
                    TimeSpan elapsed = DateTime.Now - featureExtractionStartTime;
                    workTab.LblFeatureElapsed.Text = $"경과: {FormatTimeSpan(elapsed, includeMilliseconds: true)}";
                }
                if (workTab?.LblFeatureRemaining != null)
                {
                    workTab.LblFeatureRemaining.Text = "남은 시간: 00:00:00";
                }
                if (workTab?.LblFeatureOutputPath != null)
                {
                    workTab.LblFeatureOutputPath.Text = $"출력 파일: {Path.GetFileName(result.OutputFilePath)}";
                }
                
                // 추출 완료 후 오디오 샘플레이트를 pickParam에 적용 (Live 매칭 정확도 향상)
                if (result.AudioSampleRate > 0)
                {
                    workTab.pickParam.sampleRate = result.AudioSampleRate;
                }
                // pickParam을 상세 텍스트로 출력
                workTab.pickParam.WriteToTextFile(outputFilePath, workTab.pickParam.ToDetailedText());

                AppendFeatureLog($"Original-PP 핑거프린트 추출 완료: {result.OutputFilePath} (핑거프린트 {result.TotalFingerprints}개)");
                //workTab.pickParam.Write workTab.pickParam.ToDetailedText();
                MessageBox.Show($"영화 음원 Original-PP 핑거프린트 추출이 완료되었습니다.\n핑거프린트 수: {result.TotalFingerprints}개", "완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (OperationCanceledException)
            {
                AppendFeatureLog("특징 추출이 사용자에 의해 중단되었습니다.");
                lblFeatureStatus.Text = "상태: 취소됨";
                
                // 모든 UI 컨트롤 초기화
                if (workTab?.LblFeatureProgressPercent != null)
                {
                    workTab.LblFeatureProgressPercent.Text = "진행: 0%";
                }
                if (workTab?.LblFeatureElapsed != null)
                {
                    workTab.LblFeatureElapsed.Text = "경과: 00:00:00";
                }
                if (workTab?.LblSFPTimelineCurrent != null)
                {
                    workTab.LblSFPTimelineCurrent.Text = "현재 진행: 00:00:00";
                }
                if (workTab?.LblFeatureRemaining != null)
                {
                    workTab.LblFeatureRemaining.Text = "남은 시간: --";
                }
                if (workTab?.PrgFeatureExtract != null)
                {
                    workTab.PrgFeatureExtract.Value = 0;
                }
            }
            catch (Exception ex)
            {
                AppendFeatureLog($"특징 추출 중 오류: {ex.Message}");
                lblFeatureStatus.Text = "상태: 오류";
                MessageBox.Show($"특징 추출 중 오류가 발생했습니다.\n{ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                featureExtractionPauseToken?.Dispose();
                featureExtractionPauseToken = null;
                featureExtractionPaused = false;
                featureExtractionCts?.Dispose();
                featureExtractionCts = null;
                UpdateFeatureControlsState();
            }
        }

        private async void BtnFptExtract_Click(object sender, EventArgs e)
        {
            // 진행 중이 아닐 때는 추출 시작
            if (featureExtractionCts == null)
            {
                await StartFeatureExtractionAsync();
            }
            // 진행 중이고 일시 중지되지 않았을 때는 일시 중지
            else if (!featureExtractionPaused && featureExtractionPauseToken != null)
            {
                featureExtractionPauseToken.Pause();
                featureExtractionPaused = true;
                lblFeatureStatus.Text = "상태: 일시 중지됨";
                AppendFeatureLog("특징 추출을 일시 중지했습니다. 계속하려면 'Extract' 버튼을 다시 눌러주세요.");
                UpdateFeatureControlsState();
            }
            // 일시 중지 상태일 때는 재개
            else if (featureExtractionPaused && featureExtractionPauseToken != null && featureExtractionCts != null)
            {
                featureExtractionPauseToken.Resume();
                featureExtractionPaused = false;
                lblFeatureStatus.Text = "상태: 진행 중 (재개)";
                AppendFeatureLog("특징 추출을 계속 진행합니다.");
                UpdateFeatureControlsState();
            }
        }

        private void btnCancelFeatureExtract_Click(object sender, EventArgs e)
        {
            // 현재 상태를 로컬 변수에 저장 (완전 취소를 위해)
            var cts = featureExtractionCts;
            var pauseToken = featureExtractionPauseToken;
            
            if (cts == null)
            {
                return;
            }

            if (!cts.IsCancellationRequested)
            {
                // 일시 중지 상태라면 먼저 재개한 후 취소
                if (featureExtractionPaused && pauseToken != null)
                {
                    pauseToken.Resume();
                }
                cts.Cancel();
                AppendFeatureLog("특징 추출 취소 요청을 전송했습니다.");
                
                // 완전 취소: 상태 변수 즉시 초기화 (UI가 즉시 초기화되도록)
                featureExtractionPauseToken = null;
                featureExtractionPaused = false;
                featureExtractionCts = null;
                
                // 상태 및 UI 컨트롤 초기화
                lblFeatureStatus.Text = "상태: 취소됨";
                
                // Label 컨트롤 초기화
                if (workTab?.LblFeatureProgressPercent != null)
                {
                    workTab.LblFeatureProgressPercent.Text = "진행: 0%";
                }
                if (workTab?.LblFeatureElapsed != null)
                {
                    workTab.LblFeatureElapsed.Text = "경과: 00:00:00";
                }
                if (workTab?.LblSFPTimelineCurrent != null)
                {
                    workTab.LblSFPTimelineCurrent.Text = "현재 진행: 00:00:00";
                }
                if (workTab?.LblFeatureRemaining != null)
                {
                    workTab.LblFeatureRemaining.Text = "남은 시간: --";
                }
                
                // ProgressBar 초기화
                if (workTab?.PrgFeatureExtract != null)
                {
                    workTab.PrgFeatureExtract.Value = 0;
                }
                
                UpdateFeatureControlsState();
            }
        }


        private void btnRefreshFeatureAudioFiles_Click(object sender, EventArgs e)
        {
            RefreshFeatureAudioFiles(TryGetSelectedAudioItem()?.FileName);
            AppendFeatureLog("음원 파일 목록을 새로고침했습니다.");
        }

        private async void btnConvertToWave_Click(object sender, EventArgs e)
        {
            await ConvertSelectedMp3ToWaveAsync();
        }

        private async void cmbFeatureAudioFiles_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (suppressFeatureComboEvents)
            {
                return;
            }

            UpdateFeatureControlsState();
            await UpdateSelectedAudioInfoAsync(TryGetSelectedAudioItem());
            // 선택된 영화 음원을 현재 영화의 MovieData에 기록한다.
            var selectedItem = TryGetSelectedAudioItem();
            var currentMovie = movieData.GetCurrentMovie();
            if (selectedItem != null && currentMovie != null)
            {
                // 파일명만 저장해도 RefreshFeatureAudioFiles에서 FileName/OriginalFileName 기준으로 복원 가능
                movieData.SetExtraAttribute(currentMovie.Id, ExtraKeyLastMovieAudio, selectedItem.FileName);
            }
            
            // UI 입력 정보 저장
            userEditingInfo?.SaveUIInputState();
        }

        private async Task ConvertSelectedMp3ToWaveAsync()
        {
            if (audioConversionInProgress)
            {
                return;
            }

            var selectedItem = TryGetSelectedAudioItem();
            if (selectedItem == null)
            {
                MessageBox.Show("먼저 변환할 오디오 파일을 선택하세요.", "오디오 선택 필요", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!AudioConvert.IsSupportedAudioFile(selectedItem.FullPath))
            {
                string supportedFormats = string.Join(", ", new[] { "MP3", "MP4", "MKV", "AAC", "M4A" });
                MessageBox.Show($"선택한 파일은 지원되지 않는 형식입니다.\n지원 형식: {supportedFormats}", "변환 불가", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!File.Exists(selectedItem.FullPath))
            {
                MessageBox.Show("선택한 오디오 파일을 찾을 수 없습니다. 파일이 이동되었는지 확인하세요.", "파일 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshFeatureAudioFiles();
                return;
            }

            // txtMovieTitle에 입력된 값으로 영화 폴더 먼저 생성 및 영화 정보 업데이트
            string movieFolderPath = GetOrCreateMovieFolder();
            
            var currentMovie = movieData.GetCurrentMovie();
            if (currentMovie == null)
            {
                MessageBox.Show("먼저 영화 정보를 등록하거나 선택해주세요.", "영화 정보 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string tempWavPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.wav");
            string targetFileName = MovieData.SanitizeFileName(Path.ChangeExtension(selectedItem.FileName, ".wav"));

            audioConversionInProgress = true;
            audioConversionCts?.Dispose();
            audioConversionCts = new CancellationTokenSource();
            UpdateAudioConvertControlsState();
            string fileExtension = Path.GetExtension(selectedItem.FileName).ToUpper().TrimStart('.');
            AppendFeatureLog($"{fileExtension} -> WAV 변환 시작: {selectedItem.FileName}");

            if (workTab?.LblWaveFilename != null)
            {
                workTab.LblWaveFilename.Text = "변환 중...";
            }

            SetAudioConvertProgress(workTab?.PrgAudioConvertBar?.Minimum ?? 0);
            var progress = new Progress<int>(SetAudioConvertProgress);

            try
            {
                await AudioConvert.ConvertMp3ToWavAsync(selectedItem.FullPath, tempWavPath, progress, audioConversionCts.Token);
                
                // 영화 정보가 업데이트되었을 수 있으므로 다시 가져오기
                currentMovie = movieData.GetCurrentMovie();
                if (currentMovie == null)
                {
                    MessageBox.Show("영화 정보를 가져올 수 없습니다.", "영화 정보 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                
                string sourceFormat = Path.GetExtension(selectedItem.FileName).ToUpper().TrimStart('.');
                var registered = movieData.RegisterWorkFile(
                    currentMovie.Id,
                    tempWavPath,
                    $"Converted from {sourceFormat}",
                    targetFileName,
                    overwrite: true);

                // 우리가 만든 폴더에 파일 복사 (RegisterWorkFile은 movieId 기반 폴더에 저장하므로)
                string targetFilePath = Path.Combine(movieFolderPath, targetFileName);
                try
                {
                    Directory.CreateDirectory(movieFolderPath);
                    File.Copy(tempWavPath, targetFilePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"파일을 영화 폴더에 복사하는 중 오류가 발생했습니다.\n{ex.Message}", "파일 복사 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                SetAudioConvertProgress(workTab?.PrgAudioConvertBar?.Maximum ?? 1000);
                if (workTab?.LblWaveFilename != null)
                {
                    workTab.LblWaveFilename.Text = registered.FileName;
                }
                // 현재 영화의 MovieData에 마지막 Wave 파일명 저장
                movieData.SetExtraAttribute(currentMovie.Id, ExtraKeyLastWaveFile, registered.FileName);

                AppendFeatureLog($"MP3 -> WAV 변환 완료: {registered.FileName}");
                RefreshFeatureAudioFiles(registered.FileName);
            }
            catch (OperationCanceledException)
            {
                SetAudioConvertProgress(workTab?.PrgAudioConvertBar?.Minimum ?? 0);
                if (workTab?.LblWaveFilename != null)
                {
                    workTab.LblWaveFilename.Text = "변환 취소됨";
                }

                AppendFeatureLog("MP3 -> WAV 변환이 취소되었습니다.");
            }
            catch (Exception ex)
            {
                SetAudioConvertProgress(workTab?.PrgAudioConvertBar?.Minimum ?? 0);
                if (workTab?.LblWaveFilename != null)
                {
                    workTab.LblWaveFilename.Text = "변환 실패";
                }

                AppendFeatureLog($"MP3 -> WAV 변환 실패: {ex.Message}");
                MessageBox.Show($"MP3를 WAV로 변환하는 중 오류가 발생했습니다.\n{ex.Message}", "변환 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                audioConversionInProgress = false;
                SafeDeleteFile(tempWavPath);
                audioConversionCts?.Dispose();
                audioConversionCts = null;
                UpdateAudioConvertControlsState();
            }
        }

        private void SetAudioConvertProgress(int value)
        {
            if (workTab == null || workTab.PrgAudioConvertBar == null || workTab.PrgAudioConvertBar.IsDisposed)
            {
                return;
            }

            int minimum = workTab.PrgAudioConvertBar.Minimum;
            int maximum = workTab.PrgAudioConvertBar.Maximum;
            int clamped = Math.Max(minimum, Math.Min(maximum, value));
            workTab.PrgAudioConvertBar.Value = clamped;
        }

        private static void SafeDeleteFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
                // 무시
            }
        }

        private void btnCancelConvert_Click(object sender, EventArgs e)
        {
            if (audioConversionCts == null || audioConversionCts.IsCancellationRequested)
            {
                return;
            }

            audioConversionCts.Cancel();
            if (workTab?.BtnCancelConvert != null)
            {
                workTab.BtnCancelConvert.Enabled = false;
            }
            AppendFeatureLog("MP3 -> WAV 변환 중지 요청을 전송했습니다.");
            if (workTab?.LblWaveFilename != null)
            {
                workTab.LblWaveFilename.Text = "취소 요청 중...";
            }
        }

        private void MovieTitle_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Any(IsSupportedAudioFile))
                {
                    e.Effect = DragDropEffects.Copy;
                    return;
                }
            }

            e.Effect = DragDropEffects.None;
        }

        private void MovieTitle_DragDrop(object sender, DragEventArgs e)
        {
            try
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files == null)
                {
                    return;
                }

                string audioFilePath = files.FirstOrDefault(IsSupportedAudioFile);
                if (string.IsNullOrWhiteSpace(audioFilePath))
                {
                    return;
                }

                // wave 파일이면 바로 Label 및 MovieData에 설정
                string ext = Path.GetExtension(audioFilePath)?.ToLowerInvariant();
                if (ext == ".wav")
                {
                    if (workTab?.LblWaveFilename != null)
                    {
                        workTab.LblWaveFilename.Text = Path.GetFileName(audioFilePath);
                    }

                    // 현재 영화 기준으로 마지막 Wave 파일명 저장
                    var currentMovie = movieData.GetCurrentMovie();
                    if (currentMovie != null && !string.IsNullOrWhiteSpace(currentMovie.Id))
                    {
                        string fileName = Path.GetFileName(audioFilePath);
                        movieData.SetExtraAttribute(currentMovie.Id, ExtraKeyLastWaveFile, fileName);
                        // UI 입력 정보 저장
                        userEditingInfo?.SaveUIInputState();
                    }
                }

                // 현재 폴더에 파일 복사 (MainForm.SimilarityTab.cs의 메서드 사용)
                CopyAudioFileToCurrentFolder(audioFilePath, "Drag & Drop audio file");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"파일을 처리하는 중 오류가 발생했습니다.\n{ex.Message}", "Drag & Drop 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LblMovieTitle_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "영화 음원 파일 선택";
                dialog.Filter = "Audio Files|*.wav;*.mp3";
                dialog.Multiselect = false;

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        HandleAudioFileSelection(dialog.FileName, "Manual selection audio file");
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"파일을 처리하는 중 오류가 발생했습니다.\n{ex.Message}", "파일 선택 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void HandleAudioFileSelection(string audioFilePath, string description)
        {
            if (string.IsNullOrWhiteSpace(audioFilePath))
            {
                return;
            }

            if (!IsSupportedAudioFile(audioFilePath))
            {
                MessageBox.Show("지원하지 않는 파일 형식입니다. wav 또는 mp3 파일만 가능 합니다.", "파일 형식 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string fileName = Path.GetFileName(audioFilePath);
            string titleWithoutExtension = Path.GetFileNameWithoutExtension(audioFilePath);

            string sanitizedTitle = MovieData.SanitizeFileName(titleWithoutExtension);
            txtMovieTitle.Text = sanitizedTitle;

            SetMovieFolderText(sanitizedTitle, updateCurrentName: false);

            // txtMovieTitle에 입력된 값으로 영화 폴더 먼저 생성 및 영화 정보 업데이트
            string movieFolderPath = GetOrCreateMovieFolder();

            // 영화 정보가 업데이트되었을 수 있으므로 다시 가져오기
            var currentMovie = movieData.GetCurrentMovie();
            if (currentMovie == null)
            {
                MessageBox.Show("영화 정보를 생성할 수 없습니다.", "영화 정보 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // RegisterWorkFile로 파일 등록 (메타데이터 업데이트)
            var registered = movieData.RegisterWorkFile(currentMovie.Id, audioFilePath, description, fileName, overwrite: true);
            
            // 우리가 만든 폴더에 파일 복사 (RegisterWorkFile은 movieId 기반 폴더에 저장하므로)
            string targetFilePath = Path.Combine(movieFolderPath, fileName);
            try
            {
                Directory.CreateDirectory(movieFolderPath);
                File.Copy(audioFilePath, targetFilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"파일을 영화 폴더에 복사하는 중 오류가 발생했습니다.\n{ex.Message}", "파일 복사 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            
            RefreshFeatureAudioFiles(fileName);
            AppendFeatureLog($"음원 등록: {fileName}");
            UpdateCurrentMovieTitleDisplay();
            UpdateCurrentFolderDisplay();
            
            // UI 입력 정보 저장
            userEditingInfo?.SaveUIInputState();
        }

        private static bool IsSupportedAudioFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            string extension = Path.GetExtension(filePath)?.ToLowerInvariant();
            return extension == ".wav" || extension == ".mp3";
        }

        private void AlignMovieDropAreaContents()
        {
            // pnlMovieDropArea와 lblMovieTitle이 삭제되어 더 이상 필요 없음
            // SimilarityTabControl의 pnl_MovieDropArea는 txtMovieFile을 사용하므로 정렬 불필요
        }

        private void RestoreLastMovieWork()
        {
            ResetAudioViewFileState();
            var lastMovie = movieData.GetCurrentMovie();

            // 화면표시 로컬 영화ID 초기화
            lbl_movieIdOnLocal.Text = "미등록";
            // Title
            string sanitizedTitle = string.IsNullOrWhiteSpace(lastMovie.Title)
                ? string.Empty
                : MovieData.SanitizeFileName(lastMovie.Title);
            txtMovieTitle.Text = sanitizedTitle;
            // Folder
            if (!string.IsNullOrWhiteSpace(lastMovie.Id))
            {
                string folderName = movieData.GetMovieFolderName(lastMovie.Id);
                string folderPath = movieData.GetMovieFolderPath(lastMovie.Id);

                if (string.IsNullOrWhiteSpace(folderName) || !Directory.Exists(folderPath))
                {
                    folderName = string.IsNullOrWhiteSpace(sanitizedTitle)
                        ? $"Movie_{DateTime.Now:yyyyMMdd_HHmmss}"
                        : sanitizedTitle;
                    movieData.UpdateMovieFolderName(lastMovie.Id, folderName);
                    folderPath = movieData.GetMovieFolderPath(lastMovie.Id);
                    Directory.CreateDirectory(folderPath);
                }

                folderName = MovieData.SanitizeFileName(folderName ?? string.Empty);
                SetMovieFolderText(folderName, updateCurrentName: true);

                // 서버 영화 ID 복원
                string storedServerId = movieData.GetExtraAttribute(lastMovie.Id, ExtraKeyServerMovieId);
                if (!string.IsNullOrWhiteSpace(storedServerId) && int.TryParse(storedServerId, out int parsedId) && parsedId > 0)
                {
                    currentServerMovieId = parsedId;
                    if (lbl_movieIdOnServer != null)
                    {
                        lbl_movieIdOnServer.Text = $"DB_Id( {parsedId} )"; 
                    }
                }
                else
                {
                    currentServerMovieId = null;
                    if (lbl_movieIdOnServer != null)
                    {
                        lbl_movieIdOnServer.Text = $"DB_Id( 미등록 )"; 
                    }
                }
            }
            else
            {
                // 폴더명이 없으면 빈 값으로 표시
                UpdateCurrentFolderDisplay();
                // 로컬 영화 ID는 표시 (Id가 있으면)
                //if (lbl_movieIdOnLocal != null)
                //{
                //    if (!string.IsNullOrWhiteSpace(lastMovie.Id))
                //    {
                //        lbl_movieIdOnLocal.Text = lastMovie.Id;
                //    }
                //    else
                //    {
                        lbl_movieIdOnLocal.Text = "미등록";
                //    }
                //}
            }

            txtStudioName.Text = lastMovie.Studio ?? string.Empty;
            txtDirector.Text = lastMovie.Director ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(lastMovie.ReleaseYear) &&
                int.TryParse(lastMovie.ReleaseYear, out int releaseYear))
            {
                releaseYear = Math.Max((int)nudReleaseYear.Minimum, Math.Min((int)nudReleaseYear.Maximum, releaseYear));
                nudReleaseYear.Value = releaseYear;
            }

            if (!string.IsNullOrWhiteSpace(lastMovie.Genre))
            {
                int genreIndex = cmbGenre.FindStringExact(lastMovie.Genre);
                if (genreIndex < 0)
                {
                    genreIndex = cmbGenre.FindString(lastMovie.Genre);
                }
                cmbGenre.SelectedIndex = genreIndex;
            }
            else
            {
                cmbGenre.SelectedIndex = -1;
            }

            // 마지막에 사용한 영화 음원 파일명을 MovieData에서 읽어와 콤보박스 기본 선택에 사용
            string lastMovieAudioFile = null;
            if (lastMovie != null && !string.IsNullOrWhiteSpace(lastMovie.Id))
            {
                lastMovieAudioFile = movieData.GetExtraAttribute(lastMovie.Id, ExtraKeyLastMovieAudio);
            }
            RefreshFeatureAudioFiles(lastMovieAudioFile);
            UpdateCurrentMovieTitleDisplay();
            UpdateCurrentFolderDisplay();
            
            // txtMovieFeatureName, txtLiveFeatureName 초기화
            InitializeFeatureFilePaths();

            // 마지막 Wave 파일 라벨 복원 (MovieData 기준)
            if (lastMovie != null && !string.IsNullOrWhiteSpace(lastMovie.Id))
            {
                string lastWaveFileName = movieData.GetExtraAttribute(lastMovie.Id, ExtraKeyLastWaveFile);
                if (!string.IsNullOrWhiteSpace(lastWaveFileName))
                {
                    string movieFolder = movieData.GetMovieFolderPath(lastMovie.Id);
                    string wavePath = Path.Combine(movieFolder ?? string.Empty, lastWaveFileName);
                    if (File.Exists(wavePath) && workTab?.LblWaveFilename != null)
                    {
                        workTab.LblWaveFilename.Text = lastWaveFileName;
                    }
                }
            }

            // 마지막 작업 영화의 이미지 로드
            try
            {
                if (lastMovie != null && !string.IsNullOrWhiteSpace(lastMovie.Id))
                {
                    string movieFolder = movieData.GetMovieFolderPath(lastMovie.Id);
                    string imagesFolder = Path.Combine(movieFolder, "images");
                    if (Directory.Exists(imagesFolder))
                    {
                        // images 폴더에서 가장 최근 수정된 이미지 파일을 찾아 표시
                        var imageFiles = Directory.GetFiles(imagesFolder, "*.*")
                            .Where(f => new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif" }
                                .Contains(Path.GetExtension(f).ToLower()))
                            .OrderByDescending(f => File.GetLastWriteTime(f))
                            .ToList();

                        if (imageFiles.Count > 0)
                        {
                            string imagePath = imageFiles[0];
                            using (var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read))
                            {
                                var originalImage = Image.FromStream(fs);

                                if (picMovieImage.Image != null)
                                {
                                    picMovieImage.Image.Dispose();
                                }

                                picMovieImage.Image = new Bitmap(originalImage);
                                currentMovieImagePath = imagePath;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 이미지 로드 오류는 경고만 표시하고 계속 진행
                UpdateServerStatusLabel($"마지막 영화 이미지 로드 실패: {ex.Message}", Color.Orange);
                currentMovieImagePath = null;
            }

            UpdateAudioViewFilesStatus();
            
            // UI 입력 정보 로드
            userEditingInfo?.LoadUIInputState();
        }
        
        
        private void InitializeFeatureFilePaths()
        {
            if (workTab == null)
            {
                return;
            }

            // Prof에서 저장된 Movie Feature 경로 읽기
            var savedMovieFile = prof.GetItem(PDN.S_SIMILARITY, PDN.E_FEATUREFILE, PDN.I_MOVIE);
            if (savedMovieFile.bValid && !string.IsNullOrWhiteSpace(savedMovieFile.sValue))
            {
                string moviePath = savedMovieFile.sValue;
                // 파일이 존재하면 그대로 사용, 없으면 features 폴더에서 찾기
                if (File.Exists(moviePath))
                {
                    InitializeSetMovieFeaturePath(moviePath);
                }
                else
                {
                    // 파일명만 있는 경우 features 폴더에서 찾기
                    string fileName = Path.GetFileName(moviePath);
                    if (!string.IsNullOrWhiteSpace(fileName))
                    {
                        string movieFolder = GetOrCreateMovieFolder();
                        string featuresFolder = Path.Combine(movieFolder, "features");
                        if (Directory.Exists(featuresFolder))
                        {
                            string foundPath = Path.Combine(featuresFolder, fileName);
                            if (File.Exists(foundPath))
                            {
                                InitializeSetMovieFeaturePath(foundPath);
                            }
                        }
                    }
                }
            }

            // Prof에서 저장된 Live Feature 경로 읽기
            var savedLiveFile = prof.GetItem(PDN.S_SIMILARITY, PDN.E_FEATUREFILE, PDN.I_LIVE);
            if (savedLiveFile.bValid && !string.IsNullOrWhiteSpace(savedLiveFile.sValue))
            {
                string livePath = savedLiveFile.sValue;
                // 파일이 존재하면 그대로 사용, 없으면 features 폴더에서 찾기
                if (File.Exists(livePath))
                {
                    InitializeSetLiveFeaturePath(livePath);
                }
                else
                {
                    // 파일명만 있는 경우 features 폴더에서 찾기
                    string fileName = Path.GetFileName(livePath);
                    if (!string.IsNullOrWhiteSpace(fileName))
                    {
                        string movieFolder = GetOrCreateMovieFolder();
                        string featuresFolder = Path.Combine(movieFolder, "features");
                        if (Directory.Exists(featuresFolder))
                        {
                            string foundPath = Path.Combine(featuresFolder, fileName);
                            if (File.Exists(foundPath))
                            {
                                InitializeSetLiveFeaturePath(foundPath);
                            }
                        }
                    }
                }
            }
        }
        
        private void InitializeSetMovieFeaturePath(string path)
        {
            if (workTab != null)
            {
                InitializeSetPathTextBox(workTab.TxtMovieFeaturePath, path);
                OnAViewLocalFileChanged(AudioViewFileCategory.Feature, path, registerOnServer: false);
            }
        }

        private void InitializeSetLiveFeaturePath(string path)
        {
            if (workTab != null)
            {
                InitializeSetPathTextBox(workTab.TxtLiveFeaturePath, path);
            }
        }

        private static void InitializeSetPathTextBox(TextBox textBox, string path)
        {
            if (textBox == null)
            {
                return;
            }

            string safePath = path ?? string.Empty;
            textBox.Tag = safePath;
            textBox.Text = string.IsNullOrWhiteSpace(safePath) ? string.Empty : Path.GetFileName(safePath);
        }

        private async Task LoadMoviesFromServer()
        {
            string apiUrl = ServerUrl.GetMoviesUrl(dbInfo);
            int timeoutSec = ServerUrl.GetRequestTimeoutSeconds(dbInfo);

            using (var httpClient = new HttpClient())
            {
                httpClient.Timeout = TimeSpan.FromSeconds(timeoutSec);

                try
                {
                    var response = await httpClient.GetAsync(apiUrl);
                    string responseContent = await response.Content.ReadAsStringAsync();
                    
                    if (!response.IsSuccessStatusCode)
                    {
                        // 서버에서 반환한 에러 메시지 파싱 시도
                        string errorMessage = $"서버 응답 오류: {(int)response.StatusCode} {response.ReasonPhrase}";
                        try
                        {
                            var serializer = new JavaScriptSerializer();
                            var errorObj = serializer.Deserialize<Dictionary<string, object>>(responseContent);
                            if (errorObj != null && errorObj.ContainsKey("message"))
                            {
                                errorMessage = $"서버 오류: {errorObj["message"]}";
                                if (errorObj.ContainsKey("detail"))
                                {
                                    errorMessage += $"\n상세: {errorObj["detail"]}";
                                }
                                if (errorObj.ContainsKey("code"))
                                {
                                    errorMessage += $"\n오류 코드: {errorObj["code"]}";
                                }
                            }
                        }
                        catch
                        {
                            // JSON 파싱 실패 시 원본 메시지 사용
                            if (!string.IsNullOrWhiteSpace(responseContent))
                            {
                                errorMessage += $"\n응답 내용: {responseContent}";
                            }
                        }
                        UpdateServerStatusLabel(errorMessage, Color.Red);
                        throw new Exception(errorMessage);
                    }

                    var serializer2 = new JavaScriptSerializer();
                    var result = serializer2.Deserialize<MovieListResponse>(responseContent);

                    if (result != null && result.items != null)
                    {
                        // UI 스레드에서 DataGridView 업데이트
                        Action updateGrid = () =>
                        {
                            dgv_MovieList.Rows.Clear();
                            foreach (var movie in result.items)
                            {
                                dgv_MovieList.Rows.Add(
                                    movie.id.ToString(),
                                    movie.title ?? "",
                                    movie.studio_name ?? "",
                                    movie.release_year?.ToString() ?? "",
                                    movie.director ?? "",
                                    movie.genre ?? "",
                                    movie.management_folder ?? ""
                                );
                            }

                            // 서버 응답 기준으로 전체/목록 개수를 표시한다.
                            UpdateMovieListStatus(result.total);
                        };

                        if (this.InvokeRequired)
                        {
                            this.Invoke(updateGrid);
                        }
                        else
                        {
                            updateGrid();
                        }
                    }
                }
                catch (TaskCanceledException ex)
                {
                    string errorMsg = $"서버 연결 시간 초과: {timeoutSec}초 내에 응답을 받지 못했습니다.\nURL: {apiUrl}";
                    UpdateServerStatusLabel(errorMsg, Color.Red);
                    throw new Exception(errorMsg, ex);
                }
                catch (HttpRequestException ex)
                {
                    string errorMsg = $"서버 연결 실패: {ex.Message}\n\n가능한 원인:\n1. 서버가 실행 중이지 않습니다\n2. 서버 주소나 포트가 올바르지 않습니다\n3. 방화벽이 연결을 차단하고 있습니다\n\nURL: {apiUrl}";
                    UpdateServerStatusLabel(errorMsg, Color.Red);
                    throw new Exception(errorMsg, ex);
                }
                catch (Exception ex)
                {
                    UpdateServerStatusLabel($"서버 오류: {ex.Message}", Color.Red);
                    throw;
                }
            }
        }

        // API 응답을 위한 클래스
        private class MovieListResponse
        {
            public int page { get; set; }
            public int pageSize { get; set; }
            public int total { get; set; }
            public List<MovieItem> items { get; set; }
        }

        private class MovieItem
        {
            public int id { get; set; }
            public string title { get; set; }
            public string studio_name { get; set; }
            public int? release_year { get; set; }
            public string director { get; set; }
            public string genre { get; set; }
            public string management_folder { get; set; }
            public int is_active { get; set; }
            public DateTime created_at { get; set; }
            public DateTime updated_at { get; set; }
        }

        // 서버 자산 목록 응답용 DTO
        private class MovieAssetListResponse
        {
            public List<MovieAssetItem> items { get; set; }
        }

        private class MovieAssetItem
        {
            public string type { get; set; }
            public string fileName { get; set; }
            public string url { get; set; }
            public string checksum_sha256 { get; set; }
            public long? bytes { get; set; }
        }

        /// <summary>
        /// dgv_MovieList에서 특정 행을 더블클릭했을 때,
        /// 서버에서 관리하는 영화 ID를 현재 작업 컨텍스트에 연결하고
        /// 로컬 MovieData 및 폼을 초기화한다.
        /// </summary>
        private async void Dgv_MovieList_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || dgv_MovieList == null)
            {
                return;
            }

            DataGridViewRow row = dgv_MovieList.Rows[e.RowIndex];
            if (row == null || row.Cells.Count == 0)
            {
                return;
            }

            object idValue = row.Cells[0].Value;
            if (idValue == null)
            {
                MessageBox.Show("선택한 항목에는 서버 ID가 없습니다.", "서버 ID 없음", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!int.TryParse(Convert.ToString(idValue), out int serverMovieId) || serverMovieId <= 0)
            {
                MessageBox.Show("서버 ID 형식이 올바르지 않습니다.", "서버 ID 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 로컬에 작업 중인 영화가 있는지 확인
            var currentMovie = movieData.GetCurrentMovie();
            if (currentMovie != null)
            {
                // 선택한 서버 영화 정보 가져오기
                string serverTitle = SafeGetCellText(row, 1);
                if (string.IsNullOrWhiteSpace(serverTitle))
                {
                    serverTitle = $"Movie_{serverMovieId}";
                }

                // 현재 작업 중인 영화 제목
                string currentTitle = !string.IsNullOrWhiteSpace(currentMovie.Title) ? currentMovie.Title : "제목 없음";

                // 확인 메시지
                string message = $"현재 작업 중인 영화가 있습니다.\n\n" +
                    $"현재 작업 영화: {currentTitle}\n" +
                    $"선택한 서버 영화: {serverTitle}\n\n" +
                    $"서버 영화를 가져오면 새로운 영화로 전환되며,\n" +
                    $"모든 작업 내용이 서버 영화 기준으로 변경됩니다.\n" +
                    $"계속 진행하시겠습니까?";
                
                DialogResult result = MessageBox.Show(message, "작업 중인 영화 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if (result != DialogResult.Yes)
                {
                    return; // 사용자가 취소한 경우
                }
            }

            try
            {
                await LoadServerMovieToLocalAsync(serverMovieId, row);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"서버 영화 정보를 로컬로 가져오는 중 오류가 발생했습니다.\n{ex.Message}",
                    "서버 로드 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 서버에서 관리하는 영화 ID를 현재 작업 영화와 연결하고,
        /// 폼/로컬 폴더 상태를 서버 메타데이터 기준으로 맞춘다.
        /// (현재 서버 구현은 자산 다운로드 API가 미완성이라 메타 정보 + 폴더만 준비)
        /// </summary>
        private async Task LoadServerMovieToLocalAsync(int serverMovieId, DataGridViewRow sourceRow)
        {
            // 0) 이전 영화의 파일 정보 초기화
            if (lvAudioViewFiles != null)
            {
                lvAudioViewFiles.Items.Clear();
            }
            audioViewFileItems.Clear();
            audioViewLocalPathOverrides.Clear();

            // 0-1) 이전 영화의 ExtraAttributes에서 서버 파일 경로 정보도 초기화
            // (새로운 영화 정보를 로드하기 전에 이전 영화의 정보를 정리)
            var previousMovie = movieData.GetCurrentMovie();
            if (previousMovie != null)
            {
                // 서버 파일 경로 정보 제거 (새로운 영화 로드 전에 정리)
                foreach (AudioViewFileCategory category in Enum.GetValues(typeof(AudioViewFileCategory)))
                {
                    string key = GetAudioViewAttributeKey(category);
                    movieData.SetExtraAttribute(previousMovie.Id, key, string.Empty);
                    // 체크섬도 제거
                    movieData.SetExtraAttribute(previousMovie.Id, $"{key}.Checksum", string.Empty);
                }
            }

            // 0-2) 관련 TextBox들 초기화 (먼저 초기화하여 이전 데이터 참조 방지)
            ClearAudioViewFileTextBoxes();

            // 0-3) lvAudioViewFiles 패널 재초기화
            InitializeAudioViewFilesPanel();
            
            // 0-4) 상태 업데이트를 통해 빈 상태로 표시
            UpdateAudioViewFilesStatus();

            // 1) 현재 행에서 메타데이터 읽기 (서버 목록에서 이미 내려온 값 활용)
            string title = SafeGetCellText(sourceRow, 1);
            string studio = SafeGetCellText(sourceRow, 2);
            string year = SafeGetCellText(sourceRow, 3);
            string director = SafeGetCellText(sourceRow, 4);
            string genre = SafeGetCellText(sourceRow, 5);
            string folderFromGrid = SafeGetCellText(sourceRow, 6);

            if (string.IsNullOrWhiteSpace(title))
            {
                title = $"Movie_{serverMovieId}";
            }

            // 2) 서버 영화 정보로 새로운 영화 작업 생성 (항상 새 Id 생성)
            // 기존 작업이 있어도 서버에서 다른 영화를 받으면 완전히 새로운 영화로 처리
            var info = new MovieData.MovieWorkInfo
            {
                // Id를 지정하지 않으면 UpsertMovie에서 새로 생성됨
                Title = title,
                Studio = studio,
                ReleaseYear = year,
                Director = director,
                Genre = genre,
                FolderName = !string.IsNullOrWhiteSpace(folderFromGrid) ? folderFromGrid : null,
                ExtraAttributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
            var currentMovie = movieData.UpsertMovie(info);
            movieData.SetCurrentMovie(currentMovie.Id);

            // 3) 서버 영화 ID를 로컬 ExtraAttribute에 저장해서 추후 업데이트 시 활용
            movieData.SetExtraAttribute(currentMovie.Id, ExtraKeyServerMovieId, serverMovieId.ToString());
            currentServerMovieId = serverMovieId;

            // 3-1) 서버 메타데이터 스냅샷 저장 (변경 감지용)
            var snapshot = new MovieData.ServerMetadataSnapshot
            {
                Title = title,
                Studio = studio,
                ReleaseYear = year,
                Director = director,
                Genre = genre,
                ManagementFolder = folderFromGrid,
                SnapshotAt = DateTime.Now
            };
            movieData.SetServerSnapshot(currentMovie.Id, snapshot);

            // 4) 폼 컨트롤에 서버 메타데이터 반영
            txtMovieTitle.Text = title;
            txtStudioName.Text = studio;
            txtDirector.Text = director;
            if (int.TryParse(year, out int parsedYear))
            {
                parsedYear = Math.Max((int)nudReleaseYear.Minimum, Math.Min((int)nudReleaseYear.Maximum, parsedYear));
                nudReleaseYear.Value = parsedYear;
            }
            if (!string.IsNullOrWhiteSpace(genre))
            {
                int idx = cmbGenre.Items.IndexOf(genre);
                if (idx >= 0)
                {
                    cmbGenre.SelectedIndex = idx;
                }
            }

            // 서버 관리 폴더명을 알 수 있을 때 txtMovieFolderName에 반영 (management_folder 컬럼 활용 가능 시 확장)
            // 서버에서 내려준 management_folder 가 있으면 그 값을 사용한다.
            if (!string.IsNullOrWhiteSpace(folderFromGrid))
            {
                SetMovieFolderText(folderFromGrid, updateCurrentName: true);
                try
                {
                    movieData.UpdateMovieFolderName(currentMovie.Id, folderFromGrid);
                }
                catch
                {
                    // 폴더명 메타만 업데이트하므로 예외는 무시
                }
            }

            // 5) 로컬 ID와 서버 ID를 라벨에 표시
            if (lbl_movieIdOnServer != null)
            {
                lbl_movieIdOnServer.Text = $"DB_Id( {serverMovieId} )"; //serverMovieId.ToString();
            }

            // 6) 로컬 작업 폴더를 미리 생성해두고, 향후 자산을 내려받을 준비
            try
            {
                string movieFolderPath = movieData.GetMovieFolderPath(currentMovie.Id);
                Directory.CreateDirectory(movieFolderPath);
            }
            catch
            {
                // 폴더 생성 실패는 치명적이지 않으므로 무시 (추후 작업 시 다시 시도)
            }

            // 7) 서버에서 자산(오디오/특징/자막 등) 목록을 조회하여 로컬 폴더에 다운로드
            string downloadedImagePath = await DownloadMovieAssetsAsync(serverMovieId, currentMovie.Id);

            // 7-1) 다운로드된 이미지가 있으면 표시, 없으면 로컬 폴더에서 찾기
            string imagePathToDisplay = null;
            
            if (!string.IsNullOrWhiteSpace(downloadedImagePath) && File.Exists(downloadedImagePath))
            {
                imagePathToDisplay = downloadedImagePath;
            }
            else
            {
                // 다운로드된 이미지가 없으면 로컬 폴더에서 이미지 찾기
                string movieFolder = movieData.GetMovieFolderPath(currentMovie.Id);
                string imagesFolder = Path.Combine(movieFolder, "images");
                
                if (Directory.Exists(imagesFolder))
                {
                    try
                    {
                        // images 폴더에서 이미지 파일 찾기
                        string[] imageExtensions = { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.gif" };
                        foreach (string extension in imageExtensions)
                        {
                            string[] imageFiles = Directory.GetFiles(imagesFolder, extension, SearchOption.TopDirectoryOnly);
                            if (imageFiles.Length > 0)
                            {
                                // 첫 번째 이미지 파일 사용
                                imagePathToDisplay = imageFiles[0];
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // 이미지 검색 실패는 무시
                    }
                }
            }

            // 이미지 표시
            try
            {
                // 기존 이미지 정리
                if (picMovieImage.Image != null)
                {
                    picMovieImage.Image.Dispose();
                    picMovieImage.Image = null;
                }

                if (!string.IsNullOrWhiteSpace(imagePathToDisplay) && File.Exists(imagePathToDisplay))
                {
                    // 이미지 로드
                    using (var fs = new FileStream(imagePathToDisplay, FileMode.Open, FileAccess.Read))
                    {
                        var image = Image.FromStream(fs);
                        picMovieImage.Image = new Bitmap(image);
                        currentMovieImagePath = imagePathToDisplay;
                    }
                }
                else
                {
                    // 이미지가 없으면 기존 이미지 제거
                    currentMovieImagePath = null;
                }
            }
            catch (Exception ex)
            {
                // 이미지 로드 실패는 경고만 표시
                UpdateServerStatusLabel($"이미지 표시 실패: {ex.Message}", Color.Orange);
                currentMovieImagePath = null;
            }

            // 8) 작업 폴더에서 파일을 찾아서 lvAudioViewFiles의 로컬 파일로 자동 설정
            MapFilesFromWorkFolderToAudioViewFiles(currentMovie.Id);

            // 9) SimilarityTab의 Feature 파일 목록 및 경로 초기화 (서버 영화 기준으로)
            RefreshFeatureAudioFiles();
            InitializeFeatureFilePaths();

            // 10) 변경 감지 상태 초기화
            // 서버에서 받은 이미지를 기준 이미지로 설정 (체크섬 비교용)
            serverImagePath = downloadedImagePath;
            if (!string.IsNullOrWhiteSpace(serverImagePath) && File.Exists(serverImagePath))
            {
                serverImageChecksum = CalculateFileChecksum(serverImagePath);
                currentMovieImagePath = serverImagePath;
            }
            else
            {
                serverImageChecksum = null;
            }

            CheckMetadataChanges();
            UpdateAudioViewFilesStatus();
            
            // 11) 초기 로드 시 버튼 비활성화 (변경 없음)
            if (btnRegisterMovie != null)
            {
                btnRegisterMovie.Enabled = false;
            }
        }

        private static string SafeGetCellText(DataGridViewRow row, int index)
        {
            if (row == null || index < 0 || index >= row.Cells.Count)
            {
                return string.Empty;
            }

            object value = row.Cells[index].Value;
            return value?.ToString() ?? string.Empty;
        }

        /// <summary>
        /// 서버에서 지정된 영화 ID의 자산 목록을 조회하고,
        /// 각 자산 파일을 로컬 MovieData 폴더(타입별 서브폴더)에 다운로드한다.
        /// 반환값: 다운로드된 이미지 파일 경로 (없으면 null)
        /// </summary>
        private async Task<string> DownloadMovieAssetsAsync(int serverMovieId, string localMovieId)
        {
            if (string.IsNullOrWhiteSpace(localMovieId))
            {
                return null;
            }

            string apiUrl = ServerUrl.GetMovieAssetsUrl(dbInfo, serverMovieId);
            int timeoutSec = ServerUrl.GetRequestTimeoutSeconds(dbInfo);
            string downloadedImagePath = null; // 다운로드된 이미지 경로 저장

            using (var httpClient = new HttpClient())
            {
                httpClient.Timeout = TimeSpan.FromSeconds(timeoutSec);

                HttpResponseMessage response;
                try
                {
                    response = await httpClient.GetAsync(apiUrl);
                }
                catch (Exception ex)
                {
                    // 자산 목록 조회 실패는 경고 정도로만 알림
                    UpdateServerStatusLabel($"자산 목록 조회 실패: {ex.Message}", Color.Orange);
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    UpdateServerStatusLabel($"자산 목록 조회 실패: {(int)response.StatusCode} {response.ReasonPhrase}", Color.Orange);
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync();
                MovieAssetListResponse assetList = null;
                try
                {
                    var serializer = new JavaScriptSerializer();
                    assetList = serializer.Deserialize<MovieAssetListResponse>(json);
                }
                catch
                {
                    // JSON 파싱 실패 시 무시
                }

                if (assetList?.items == null || assetList.items.Count == 0)
                {
                    // 자산이 없는 경우 조용히 반환
                    return null;
                }

                string movieFolder = movieData.GetMovieFolderPath(localMovieId);

                // 유효한 Asset만 필터링
                var validAssets = assetList.items
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(a.url) && !string.IsNullOrWhiteSpace(a.fileName))
                    .ToList();

                int totalCount = validAssets.Count;
                int successCount = 0;
                int failCount = 0;

                if (totalCount > 0)
                {
                    UpdateServerStatusLabel($"자산 다운로드 시작: 총 {totalCount}개", Color.Blue);
                }

                // 순차적으로 다운로드
                for (int i = 0; i < validAssets.Count; i++)
                {
                    var asset = validAssets[i];
                    int currentIndex = i + 1;

                    // 타입별 서브폴더 분류
                    string typeFolder = NormalizeAssetTypeFolder(asset.type);
                    string targetFolder = string.IsNullOrWhiteSpace(typeFolder)
                        ? movieFolder
                        : Path.Combine(movieFolder, typeFolder);

                    try
                    {
                        Directory.CreateDirectory(targetFolder);
                    }
                    catch (Exception ex)
                    {
                        UpdateServerStatusLabel($"폴더 생성 실패 ({asset.fileName}): {ex.Message}", Color.Orange);
                        failCount++;
                        continue;
                    }

                    string targetPath = Path.Combine(targetFolder, MovieData.SanitizeFileName(asset.fileName));

                    try
                    {
                        // 이미지 타입인 경우 동일 파일 체크
                        bool isImageType = string.Equals(asset.type, "image", StringComparison.OrdinalIgnoreCase);
                        bool skipDownload = false;

                        // 서버에서 체크섬 정보를 받은 경우, 기존 파일과 비교하여 동일 파일인지 확인
                        if (File.Exists(targetPath) && !string.IsNullOrWhiteSpace(asset.checksum_sha256))
                        {
                            try
                            {
                                string localChecksum = CalculateFileChecksum(targetPath);
                                if (!string.IsNullOrWhiteSpace(localChecksum) && 
                                    string.Equals(localChecksum, asset.checksum_sha256, StringComparison.OrdinalIgnoreCase))
                                {
                                    // 체크섬이 동일하면 다운로드 건너뛰기
                                    skipDownload = true;
                                    UpdateServerStatusLabel($"파일 건너뜀 ({currentIndex}/{totalCount}): {asset.fileName} (체크섬 일치)", Color.Gray);
                                    
                                    // 이미지 타입인 경우 경로 저장
                                    if (isImageType)
                                    {
                                        downloadedImagePath = targetPath;
                                    }
                                    
                                    // 서버 체크섬 저장
                                    AudioViewFileCategory? category = ConvertAssetTypeToCategory(asset.type);
                                    if (category.HasValue)
                                    {
                                        SetServerFileChecksum(category.Value, asset.checksum_sha256);
                                        SetServerRegisteredFile(category.Value, targetPath);
                                    }
                                    
                                    successCount++;
                                }
                            }
                            catch
                            {
                                // 체크섬 계산 실패 시 다운로드 진행
                                skipDownload = false;
                            }
                        }

                        if (!skipDownload)
                        {
                            // 진행 상황 표시
                            UpdateServerStatusLabel($"다운로드 중 ({currentIndex}/{totalCount}): {asset.fileName}", Color.Blue);

                            // 절대 URL/상대 URL 모두 지원
                            string baseUrl = ServerUrl.GetServerBaseUrl(dbInfo);
                            string downloadUrl = asset.url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                ? asset.url
                                : $"{baseUrl.TrimEnd('/')}/{asset.url.TrimStart('/')}";

                            using (var fileResponse = await httpClient.GetAsync(downloadUrl))
                            {
                                if (!fileResponse.IsSuccessStatusCode)
                                {
                                    UpdateServerStatusLabel($"다운로드 실패 ({currentIndex}/{totalCount}): {asset.fileName} - HTTP {(int)fileResponse.StatusCode}", Color.Orange);
                                    failCount++;
                                    continue;
                                }

                                using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                                {
                                    await fileResponse.Content.CopyToAsync(fs);
                                }

                                // 서버에서 받은 체크섬을 우선 사용, 없으면 로컬에서 계산
                                string checksum = !string.IsNullOrWhiteSpace(asset.checksum_sha256)
                                    ? asset.checksum_sha256
                                    : CalculateFileChecksum(targetPath);
                                
                                if (!string.IsNullOrWhiteSpace(checksum) && !string.IsNullOrWhiteSpace(asset.type))
                                {
                                    // 이미지 타입인 경우 경로 저장
                                    if (isImageType)
                                    {
                                        downloadedImagePath = targetPath;
                                    }

                                    // Asset 타입을 AudioViewFileCategory로 변환
                                    AudioViewFileCategory? category = ConvertAssetTypeToCategory(asset.type);
                                    if (category.HasValue)
                                    {
                                        // 서버에서 받은 체크섬 저장 (서버의 실제 체크섬)
                                        SetServerFileChecksum(category.Value, checksum);
                                        // 서버 파일 경로도 저장
                                        SetServerRegisteredFile(category.Value, targetPath);
                                    }
                                }

                                successCount++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        UpdateServerStatusLabel($"다운로드 실패 ({currentIndex}/{totalCount}): {asset.fileName} - {ex.Message}", Color.Orange);
                        failCount++;
                    }
                }

                // 다운로드 완료 결과 표시
                if (totalCount > 0)
                {
                    if (failCount == 0)
                    {
                        UpdateServerStatusLabel($"자산 다운로드 완료: {successCount}개 성공", Color.Green);
                    }
                    else
                    {
                        UpdateServerStatusLabel($"자산 다운로드 완료: {successCount}개 성공, {failCount}개 실패", Color.Orange);
                    }
                }
            }

            // 자산 다운로드 후, 특징 추출/오디오 콤보박스 및 상태를 갱신
            RefreshFeatureAudioFiles();
            UpdateAudioViewFilesStatus();

            // 다운로드된 이미지 경로 반환
            return downloadedImagePath;
        }

        /// <summary>
        /// 자산 type 문자열을 로컬 서브폴더 이름으로 변환한다.
        /// </summary>
        private static string NormalizeAssetTypeFolder(string type)
        {
            if (string.IsNullOrWhiteSpace(type))
            {
                return null;
            }

            switch (type)
            {
                case "audio_original":
                    return "audio_original";
                case "audio_feature":
                    return "features";
                case "audio_desc_ko":
                    return "audio_desc_ko";
                case "audio_desc_en":
                    return "audio_desc_en";
                case "subtitle_ko":
                    return "subtitle_ko";
                case "subtitle_en":
                    return "subtitle_en";
                case "image":
                    return "images";
                default:
                    return type.ToLowerInvariant();
            }
        }
        private void Btn_popSettingForm_Click(object sender, EventArgs e)
        {
            // settingsForm 인스턴스 생성 및 표시
            SettingsForm settingsForm = new SettingsForm();
            settingsForm.pa = this;
            settingsForm.Show();
        }

        private void Btn_popSearchForm_Click(object sender, EventArgs e)
        {
            // 영화 검색 Dialog 생성 및 표시
            using (MovieSearchForm searchForm = new MovieSearchForm())
            {
                searchForm.ParentMainForm = this;
                searchForm.ShowDialog(this);
            }
        }

        private async void btnRegisterMovie_Click(object sender, EventArgs e)
        {
            try
            {
                await RegisterMovieAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"영화 등록 중 오류가 발생했습니다.\n{ex.Message}", "등록 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnResetMovieForm_Click(object sender, EventArgs e)
        {
            ResetMovieForm();
        }

        /// <summary>
        /// 새 영화 작업 시작: 현재 작업 중인 영화 컨텍스트를 초기화하고 폼을 리셋한다.
        /// (로컬 MovieData 인덱스는 유지하지만, CurrentMovieId 를 비워 새 작업으로 전환)
        /// </summary>
        private void btnNewMovieWork_Click(object sender, EventArgs e)
        {
            // 기존 영화 작업 이력을 모두 초기화 (새 프로젝트 시작 개념)
            movieData.ClearAllMovies(deleteFiles: false);

            // 서버 영화 ID 상태 초기화
            currentServerMovieId = null;
            if (lbl_movieIdOnServer != null)
            {
                lbl_movieIdOnServer.Text = $"DB_Id( 미등록 )"; 
            }
            // 로컬 영화 ID 상태 초기화
            if (lbl_movieIdOnLocal != null)
            {
                lbl_movieIdOnLocal.Text = "미등록";
            }

            // 폼 및 로컬 상태 초기화
            ResetMovieForm();
            UpdateCurrentMovieTitleDisplay();
            UpdateCurrentFolderDisplay();

            // 작업 폴더가 사라졌으므로 로컬 AudioView 파일 상태도 모두 리셋
            ResetAudioViewFileState();
        }

        private void txtMovieFolderName_TextChanged(object sender, EventArgs e)
        {
            if (suppressMovieFolderTextChange)
            {
                return;
            }
            UpdateCurrentFolderDisplay();
        }


        private void SetMovieFolderText(string value, bool updateCurrentName)
        {
            if (txtMovieFolderName == null)
            {
                return;
            }

            string sanitized = MovieData.SanitizeFileName(value ?? string.Empty);

            suppressMovieFolderTextChange = true;
            txtMovieFolderName.Text = sanitized;
            suppressMovieFolderTextChange = false;

            if (updateCurrentName)
            {
                currentMovieFolderName = string.IsNullOrWhiteSpace(sanitized) ? null : sanitized;
            }

            UpdateCurrentFolderDisplay();
        }

        private void tabControl1_DrawItem(object sender, DrawItemEventArgs e)
        {
            TabControl tabControl = sender as TabControl;
            TabPage tabPage = tabControl.TabPages[e.Index];
            
            // 선택된 탭인지 확인
            bool isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

            // 색상 설정
            //Color backColor = isSelected ? Color.Orange : SystemColors.Control;
            //Color textColor = isSelected ? Color.White : Color.Black;
            Color backColor = isSelected ? Color.FromArgb(255, 169, 50, 3) : Color.FromArgb(255, 201, 216, 216);
            Color textColor = isSelected ? Color.White : Color.FromArgb(255, 50, 67, 67);
            
            // 배경 그리기
            using (SolidBrush backBrush = new SolidBrush(backColor))
            {
                e.Graphics.FillRectangle(backBrush, e.Bounds);
            }
            
            // 텍스트 그리기
            TextRenderer.DrawText(e.Graphics, tabPage.Text, tabControl.Font, e.Bounds, textColor, 
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private void InitializeMovieDataGrid()
        {
            // DataGridView 컬럼 설정
            dgv_MovieList.Columns.Clear();
            
            // ID 컬럼 (서버에서 관리하는 영화 ID)
            DataGridViewTextBoxColumn idColumn = new DataGridViewTextBoxColumn();
            idColumn.Name = "Id";
            idColumn.HeaderText = "ID";
            idColumn.Width = 60;
            idColumn.ReadOnly = true;
            dgv_MovieList.Columns.Add(idColumn);

            // 제목 컬럼
            DataGridViewTextBoxColumn titleColumn = new DataGridViewTextBoxColumn();
            titleColumn.Name = "Title";
            titleColumn.HeaderText = "제목";
            titleColumn.Width = 120;
            titleColumn.ReadOnly = true;
            dgv_MovieList.Columns.Add(titleColumn);

            // 제작사 컬럼
            DataGridViewTextBoxColumn studioColumn = new DataGridViewTextBoxColumn();
            studioColumn.Name = "Studio";
            studioColumn.HeaderText = "제작사";
            studioColumn.Width = 100;
            studioColumn.ReadOnly = true;
            dgv_MovieList.Columns.Add(studioColumn);

            // 개봉년도 컬럼
            DataGridViewTextBoxColumn yearColumn = new DataGridViewTextBoxColumn();
            yearColumn.Name = "Year";
            yearColumn.HeaderText = "개봉년도";
            yearColumn.Width = 70;
            yearColumn.ReadOnly = true;
            dgv_MovieList.Columns.Add(yearColumn);

            // 감독 컬럼
            DataGridViewTextBoxColumn directorColumn = new DataGridViewTextBoxColumn();
            directorColumn.Name = "Director";
            directorColumn.HeaderText = "감독";
            directorColumn.Width = 90;
            directorColumn.ReadOnly = true;
            dgv_MovieList.Columns.Add(directorColumn);

            // 장르 컬럼
            DataGridViewTextBoxColumn genreColumn = new DataGridViewTextBoxColumn();
            genreColumn.Name = "Genre";
            genreColumn.HeaderText = "장르";
            genreColumn.Width = 80;
            genreColumn.ReadOnly = true;
            dgv_MovieList.Columns.Add(genreColumn);

            // 작업 폴더 컬럼 (서버 management_folder 를 보관하기 위한 숨김 컬럼)
            DataGridViewTextBoxColumn folderColumn = new DataGridViewTextBoxColumn();
            folderColumn.Name = "Folder";
            folderColumn.HeaderText = "작업폴더";
            folderColumn.Width = 100;
            folderColumn.ReadOnly = true;
            folderColumn.Visible = false; // 화면에는 표시하지 않음
            dgv_MovieList.Columns.Add(folderColumn);

            // 헤더 스타일 설정
            dgv_MovieList.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(255, 30, 30, 30); // (255, 169, 50, 3);
            dgv_MovieList.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            dgv_MovieList.ColumnHeadersDefaultCellStyle.Font = new Font("맑은고딕", 9F, FontStyle.Bold);
            dgv_MovieList.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;

            // 행 스타일 설정
            dgv_MovieList.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(255, 230, 230, 230); // (255, 248, 248, 248);
            dgv_MovieList.DefaultCellStyle.Font = new Font("맑은고딕", 8F);
            dgv_MovieList.DefaultCellStyle.SelectionBackColor = Color.FromArgb(255, 10, 50, 170);
            dgv_MovieList.DefaultCellStyle.SelectionForeColor = Color.White;
        }

        private void LoadSampleMovieData()
        {
            try
            {
                // DB에서 영화 데이터 로드 시도
                LoadMoviesFromDatabase();
            }
            catch (Exception)
            {
                // DB 연결 실패 시 샘플 데이터 로드
                LoadSampleData();
            }
        }

        private void LoadMoviesFromDatabase()
        {
            string connectionString = dbInfo.GetConnectionString();
            
            string query = "SELECT Title, StudioName AS Studio, ReleaseYear, Director, Genre FROM Movies ORDER BY Title";

            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                connection.Open();
                
                using (SqlCommand command = new SqlCommand(query, connection))
                {
                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        dgv_MovieList.Rows.Clear();
                        while (reader.Read())
                        {
                            dgv_MovieList.Rows.Add(
                                string.Empty, // 로컬 DB에는 ID 정보가 없으므로 공란
                                reader["Title"].ToString(),
                                reader["Studio"].ToString(),
                                reader["ReleaseYear"].ToString(),
                                reader["Director"].ToString(),
                                reader["Genre"].ToString(),
                                string.Empty // management_folder 없음
                            );
                        }
                    }
                }
            }

            // 로컬 DB에서 전체 목록을 불러온 것이므로,
            // 서버 총 개수 정보는 따로 없고 현재 그리드 개수로 표시한다.
            UpdateMovieListStatus(null);
        }

        private void LoadSampleData()
        {
            // 샘플 영화 데이터
            var sampleMovies = new List<MovieInfo>
            {
                new MovieInfo("어벤져스", "마블 스튜디오", "2012", "조 루소", "액션"),
                new MovieInfo("어벤져스: 엔드게임", "마블 스튜디오", "2019", "조 루소", "액션"),
                new MovieInfo("아바타", "20세기 스튜디오", "2009", "제임스 카메론", "SF"),
                new MovieInfo("타이타닉", "20세기 스튜디오", "1997", "제임스 카메론", "드라마"),
                new MovieInfo("겨울왕국", "월트 디즈니 애니메이션 스튜디오", "2013", "크리스 벅", "애니메이션"),
                new MovieInfo("기생충", "바른손이앤에이", "2019", "봉준호", "드라마"),
                new MovieInfo("인터스텔라", "리스 버틀러 프로덕션", "2014", "크리스토퍼 놀란", "SF"),
                new MovieInfo("인셉션", "싱크로필름", "2010", "크리스토퍼 놀란", "SF"),
                new MovieInfo("토르", "마블 스튜디오", "2011", "케네스 브래나", "액션"),
                new MovieInfo("아이언맨", "마블 스튜디오", "2008", "존 파브로", "액션")
            };

            foreach (var movie in sampleMovies)
            {
                dgv_MovieList.Rows.Add(
                    string.Empty,          // 샘플 데이터에는 ID 없음
                    movie.Title,
                    movie.Studio,
                    movie.Year,
                    movie.Director,
                    movie.Genre,
                    string.Empty           // management_folder 없음
                );
            }

            // 샘플 데이터일 경우에도 현재 그리드 개수 기준으로 현황을 표시한다.
            UpdateMovieListStatus(null);
        }

        public void UpdateMovieDataGrid(List<MovieInfo> searchResults)
        {
            // 기존 데이터 클리어
            dgv_MovieList.Rows.Clear();

            // 검색 결과를 DataGridView에 추가
            foreach (MovieInfo movie in searchResults)
            {
                dgv_MovieList.Rows.Add(
                    string.Empty,          // 검색 결과도 현재는 ID 미사용
                    movie.Title,   // 제목
                    movie.Studio,  // 제작사
                    movie.Year,    // 개봉년도
                    movie.Director,// 감독
                    movie.Genre,   // 장르
                    string.Empty   // management_folder 없음
                );
            }

            // 검색 결과 기준으로 그리드 개수를 다시 표시한다.
            UpdateMovieListStatus(null);
        }

        /// <summary>
        /// DB 서버에 저장된 전체 영화 개수와 현재 dgv_MovieList에 표시 중인 개수를
        /// flowLayout1 영역의 라벨에 표시한다.
        /// totalFromServer 가 null 이면, 서버 총 개수는 현재 그리드 개수와 동일하게 취급한다.
        /// </summary>
        /// <param name="totalFromServer">DB 서버에 저장된 전체 영화 개수 (알 수 없으면 null)</param>
        private void UpdateMovieListStatus(int? totalFromServer)
        {
            if (lblMovieCountServer == null || lblMovieCountGrid == null || dgv_MovieList == null)
            {
                return;
            }

            Action updateAction = () =>
            {
                int gridCount = dgv_MovieList.Rows.Count;
                int total = totalFromServer ?? gridCount;

                lblMovieCountServer.Text = $"DB: {total}편";
                lblMovieCountGrid.Text = $"목록: {gridCount}편";
            };

            if (InvokeRequired)
            {
                try
                {
                    Invoke(updateAction);
                }
                catch
                {
                    // 폼이 이미 dispose 된 경우 등은 무시
                }
            }
            else
            {
                updateAction();
            }
        }

        /// <summary>
        /// 영화 목록 상태 패널의 폭이 변경될 때,
        /// 서버/목록 개수 라벨이 패널 폭을 조정한다.
        /// </summary>
        private void pnMovieltemStatus_SizeChanged(object sender, EventArgs e)
        {
            if (pnMovieltemStatus == null || lblMovieCountServer == null || lblMovieCountGrid == null)
            {
                return;
            }

            int panelWidth = pnMovieltemStatus.ClientSize.Width;
            if (panelWidth <= 0)
            {
                return;
            }

            // 좌우 여백을 약간 두고 1/2씩 분배
            int margin = 6;
            int available = Math.Max(0, panelWidth - margin * 2);
            int halfWidth = available / 2;

            // 서버 개수 라벨: 왼쪽
            lblMovieCountServer.Left = margin;
            lblMovieCountServer.Width = halfWidth;

            // 목록 개수 라벨: 오른쪽
            lblMovieCountGrid.Left = margin + halfWidth;
            lblMovieCountGrid.Width = panelWidth - lblMovieCountGrid.Left - margin;
        }


        private async Task CheckServerConnection()
        {
            if (txtMessageFromServer == null)
            {
                return;
            }

            try
            {
                string healthUrl = ServerUrl.GetHealthUrl(dbInfo);
                int timeoutSec = ServerUrl.GetRequestTimeoutSeconds(dbInfo);

                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(timeoutSec);
                    
                    try
                    {
                        var response = await httpClient.GetAsync(healthUrl);
                        if (response.IsSuccessStatusCode)
                        {
                            UpdateServerStatusLabel("서버 연결됨", Color.Green, true);

                            // 서버 연결에 성공했고 마지막 작업 영화에 서버 ID가 있으면
                            // 서버 Asset(이미지 포함)의 체크섬을 가져와 로컬과 상태를 비교/표시한다.
                            if (currentServerMovieId.HasValue)
                            {
                                try
                                {
                                    _ = RefreshServerChecksumsAsync(currentServerMovieId.Value);
                                }
                                catch
                                {
                                    // 백그라운드 작업 실패는 무시 (로그는 내부에서 처리)
                                }
                            }
                        }
                        else
                        {
                            UpdateServerStatusLabel($"서버 응답 오류: {(int)response.StatusCode} {response.ReasonPhrase}", Color.Orange, false);
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        UpdateServerStatusLabel("서버 연결 시간 초과", Color.Red);
                    }
                    catch (HttpRequestException ex)
                    {
                        UpdateServerStatusLabel($"서버 연결 실패: {ex.Message}", Color.Red);
                    }
                    catch (Exception ex)
                    {
                        UpdateServerStatusLabel($"서버 오류: {ex.Message}", Color.Red);
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                UpdateServerStatusLabel($"서버 설정 오류: {ex.Message}", Color.Orange);
            }
            catch (Exception ex)
            {
                UpdateServerStatusLabel($"오류: {ex.Message}", Color.Red);
            }
        }

        private void UpdateServerStatusLabel(string message, Color color, bool? connectionState = null)
        {
            // serverConnected 플래그는, 명시적으로 true/false를 전달한 경우에만 변경한다.
            // (기존 호출부는 세 번째 인자를 생략하므로, 단순 로그 출력 시에는 상태를 그대로 유지한다.)
            if (connectionState.HasValue)
            {
                serverConnected = connectionState.Value;
            }

            if (txtMessageFromServer == null)
            {
                return;
            }

            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() =>
                {
                    // 기존 텍스트가 있으면 줄바꿈 추가, 없으면 그냥 추가
                    if (string.IsNullOrEmpty(txtMessageFromServer.Text))
                    {
                        txtMessageFromServer.Text = message;
                    }
                    else
                    {
                        txtMessageFromServer.AppendText(Environment.NewLine + message);
                    }
                    txtMessageFromServer.ForeColor = color;
                    // TextBox의 배경색도 상태에 따라 변경
                    if (color == Color.Green)
                    {
                        txtMessageFromServer.BackColor = Color.FromArgb(240, 255, 240); // 연한 초록색
                    }
                    else if (color == Color.Red)
                    {
                        txtMessageFromServer.BackColor = Color.FromArgb(255, 240, 240); // 연한 빨간색
                    }
                    else if (color == Color.Orange)
                    {
                        txtMessageFromServer.BackColor = Color.FromArgb(255, 248, 220); // 연한 주황색
                    }
                    else
                    {
                        txtMessageFromServer.BackColor = System.Drawing.SystemColors.Window;
                    }
                    // 새로 추가된 메시지로 스크롤
                    txtMessageFromServer.SelectionStart = txtMessageFromServer.Text.Length;
                    txtMessageFromServer.ScrollToCaret();
                }));
            }
            else
            {
                // 기존 텍스트가 있으면 줄바꿈 추가, 없으면 그냥 추가
                if (string.IsNullOrEmpty(txtMessageFromServer.Text))
                {
                    txtMessageFromServer.Text = message;
                }
                else
                {
                    txtMessageFromServer.AppendText(Environment.NewLine + message);
                }
                txtMessageFromServer.ForeColor = color;
                // TextBox의 배경색도 상태에 따라 변경
                if (color == Color.Green)
                {
                    txtMessageFromServer.BackColor = Color.FromArgb(240, 255, 240); // 연한 초록색
                }
                else if (color == Color.Red)
                {
                    txtMessageFromServer.BackColor = Color.FromArgb(255, 240, 240); // 연한 빨간색
                }
                else if (color == Color.Orange)
                {
                    txtMessageFromServer.BackColor = Color.FromArgb(255, 248, 220); // 연한 주황색
                }
                else
                {
                    txtMessageFromServer.BackColor = System.Drawing.SystemColors.Window;
                }
                // 새로 추가된 메시지로 스크롤
                txtMessageFromServer.SelectionStart = txtMessageFromServer.Text.Length;
                txtMessageFromServer.ScrollToCaret();
            }
        }

        private void ResetMovieForm()
        {
            txtMovieTitle.Text = string.Empty;
            txtStudioName.Text = string.Empty;
            txtDirector.Text = string.Empty;
            cmbGenre.SelectedIndex = -1;
            SetMovieFolderText(string.Empty, updateCurrentName: true);

            int currentYear = DateTime.Now.Year;
            int yearToSet = Math.Max((int)nudReleaseYear.Minimum, Math.Min((int)nudReleaseYear.Maximum, currentYear));
            nudReleaseYear.Value = yearToSet;

            // 이미지 초기화
            if (picMovieImage.Image != null)
            {
                picMovieImage.Image.Dispose();
                picMovieImage.Image = null;
            }
            currentMovieImagePath = null;
            serverImagePath = null;
            serverImageChecksum = null;

            // 이미지 버튼 텍스트 초기화
            if (btnPickMovieImage != null)
            {
                btnPickMovieImage.Text = "이미지 선택";
            }

            txtMovieTitle.Focus();
            ResetAudioViewFileState();
        }

        private async Task RegisterMovieAsync()
        {
            string title = txtMovieTitle.Text.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                MessageBox.Show("영화 제목을 입력해주세요.", "입력 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtMovieTitle.Focus();
                return;
            }
            string managementFolder = txtMovieFolderName.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(managementFolder))
            {
                MessageBox.Show("관리 폴더명을 입력해주세요.", "입력 확인", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtMovieFolderName.Focus();
                return;
            }
            // 폴더명은 여기에서 한 번만 정규화하여, 이후 로컬/서버에 동일하게 사용한다.
            managementFolder = MovieData.SanitizeFileName(managementFolder);

            string studio = txtStudioName.Text.Trim();
            string director = txtDirector.Text.Trim();
            string genre = cmbGenre.SelectedIndex >= 0 ? cmbGenre.SelectedItem.ToString() : string.Empty;

            // ★ 여기에서 MovieData에 현재 영화 정보와 폴더명을 먼저 반영
            var currentMovie = movieData.GetCurrentMovie();
            if (currentMovie == null)
            {
                var info = new MovieData.MovieWorkInfo
                {
                    Title = title,
                    Studio = studio,
                    ReleaseYear = ((int)nudReleaseYear.Value).ToString(),
                    Director = director,
                    Genre = genre
                };
                currentMovie = movieData.UpsertMovie(info);
                movieData.SetCurrentMovie(currentMovie.Id);
            }

            // 폴더명 변경 처리 (서버 등록 전에 수행)
            // 등록 시에는 항상 사용자가 입력한 폴더명으로 업데이트
            try
            {
                if (currentMovie != null)
                {
                    string currentName = movieData.GetMovieFolderName(currentMovie.Id) ?? currentMovie.Id;
                    bool folderNameChanged = !string.Equals(currentName, managementFolder, StringComparison.OrdinalIgnoreCase);
                    
                    if (folderNameChanged)
                    {
                        string oldPath = movieData.GetMovieFolderPath(currentMovie.Id);
                        string basePath = Path.GetDirectoryName(oldPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) 
                                            ?? Path.Combine(Application.StartupPath, "MovieData");
                        Directory.CreateDirectory(basePath);
                        string newPath = Path.Combine(basePath, managementFolder);

                        DialogResult dr = DialogResult.No;
                        if (Directory.Exists(newPath))
                        {
                            string msg = $"'{managementFolder}' 이름의 폴더가 이미 존재합니다.";
                            msg += $"\n\n 변경된 폴더'{managementFolder}'에서 등록을 진행할까요?";
                            dr = MessageBox.Show(msg, "폴더 이름 중복", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                            if(dr != DialogResult.Yes) return;
                            msg = $"'{managementFolder}' 폴더 내용을 모두 삭제하고 진행할까요?";
                            dr = MessageBox.Show(msg, "폴더 이름 중복", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                            if (dr != DialogResult.Yes) return;
                            Directory.Delete(newPath, recursive: true);
                        }

                        var selectedBefore = TryGetSelectedAudioItem()?.FileName;

                        if (Directory.Exists(oldPath))
                        {
                            Directory.Move(oldPath, newPath); // 폴더명 변경
                            if (Directory.Exists(oldPath))
                            { Directory.Delete(oldPath, recursive: true); } // 과거 폴더 삭제
                        }
                        else if (dr != DialogResult.Yes)
                        {
                            Directory.CreateDirectory(newPath);
                        }

                        RefreshFeatureAudioFiles(selectedBefore);
                    }
                    
                    // 등록 시에는 항상 입력한 폴더명으로 업데이트 (폴더명이 같아도 최신 값으로 갱신)
                    movieData.UpdateMovieFolderName(currentMovie.Id, managementFolder);
                    SetMovieFolderText(managementFolder, updateCurrentName: true);
                    if (folderNameChanged)
                    {
                        AppendFeatureLog($"영화 폴더 이름을 '{managementFolder}'(으)로 변경했습니다.");
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"영화 폴더를 준비하는 중 오류가 발생했습니다.\n{ex.Message}", "폴더 생성 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }


            // 변경된 필드만 서버로 전송 (스냅샷과 비교)
            var payload = new Dictionary<string, object>();
            var snapshot = movieData.GetServerSnapshot(currentMovie.Id);
            
            if (snapshot == null)
            {
                // 스냅샷이 없으면 전체 필드 전송 (신규 등록)
                payload["title"] = title;
                payload["releaseYear"] = (int)nudReleaseYear.Value;
                payload["isActive"] = true;
                if (!string.IsNullOrWhiteSpace(studio))
                {
                    payload["studioName"] = studio;
                }
                if (!string.IsNullOrWhiteSpace(director))
                {
                    payload["director"] = director;
                }
                if (!string.IsNullOrWhiteSpace(genre))
                {
                    payload["genre"] = genre;
                }
                payload["managementFolder"] = managementFolder;
            }
            else
            {
                // 변경된 필드만 전송
                if (!string.Equals(title, snapshot.Title ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    payload["title"] = title;
                }
                
                string yearStr = ((int)nudReleaseYear.Value).ToString();
                if (!string.Equals(yearStr, snapshot.ReleaseYear ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    payload["releaseYear"] = (int)nudReleaseYear.Value;
                }
                
                if (!string.IsNullOrWhiteSpace(studio) && !string.Equals(studio, snapshot.Studio ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    payload["studioName"] = studio;
                }
                
                if (!string.IsNullOrWhiteSpace(director) && !string.Equals(director, snapshot.Director ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    payload["director"] = director;
                }
                
                if (!string.IsNullOrWhiteSpace(genre) && !string.Equals(genre, snapshot.Genre ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    payload["genre"] = genre;
                }
                
                if (!string.Equals(managementFolder, snapshot.ManagementFolder ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                {
                    payload["managementFolder"] = managementFolder;
                }
                
                // 변경된 필드가 없으면 메타데이터는 업데이트 불필요이지만,
                // 이미지가 서버와 다른지 추가로 확인해야 한다.
                if (payload.Count == 0)
                {
                    // 1) 서버 쪽 이미지 Asset이 존재하는지, 있다면 체크섬이 무엇인지 갱신
                    if (currentServerMovieId.HasValue && serverConnected)
                    {
                        UpdateServerStatusLabel("메타데이터 변경 없음, 서버 이미지와 로컬 이미지 비교를 진행합니다.", Color.Gray);
                        try
                        {
                            // 최신 Asset 목록/체크섬을 서버에서 받아온다.
                            await RefreshServerChecksumsAsync(currentServerMovieId.Value);
                        }
                        catch
                        {
                            // 비교 실패는 무시하고 이후 로직에서 다시 판단
                        }
                    }

                    // 2) 이미지 비교: 서버 기준 이미지 vs 현재 로컬 이미지
                    bool hasLocalImage = !string.IsNullOrWhiteSpace(currentMovieImagePath) && File.Exists(currentMovieImagePath);
                    bool imageDiffers = false;

                    try
                    {
                        if (!string.IsNullOrWhiteSpace(serverImageChecksum))
                        {
                            // 서버 기준 이미지가 있고 체크섬이 있으면, 현재 이미지와 체크섬 비교
                            string currentImageChecksum = null;
                            if (hasLocalImage)
                            {
                                currentImageChecksum = CalculateFileChecksum(currentMovieImagePath);
                            }

                            if (!string.IsNullOrWhiteSpace(currentImageChecksum) &&
                                !string.Equals(currentImageChecksum, serverImageChecksum, StringComparison.OrdinalIgnoreCase))
                            {
                                imageDiffers = true;
                                UpdateServerStatusLabel("서버 이미지와 로컬 이미지의 체크섬이 다릅니다. 이미지 재등록이 필요합니다.", Color.Orange);
                            }
                        }
                        else
                        {
                            // 서버 기준 이미지가 없는데 로컬에 이미지는 있는 경우 → 업로드 필요
                            if (hasLocalImage)
                            {
                                imageDiffers = true;
                                UpdateServerStatusLabel("서버에 이미지가 없고 로컬에는 이미지가 있습니다. 이미지 등록이 필요합니다.", Color.Orange);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        UpdateServerStatusLabel($"이미지 비교 중 오류가 발생했습니다: {ex.Message}", Color.Orange);
                    }

                    // 3) 메타데이터도 없고 이미지도 동일하면 완전히 변경 없음
                    if (!imageDiffers)
                    {
                        UpdateServerStatusLabel("변경된 내용이 없습니다. (메타데이터 및 이미지 모두 동일)", Color.Gray);
                        CheckMetadataChanges();
                        return;
                    }

                    // 이미지가 다르면 payload는 비어 있어도 이후 이미지 업로드 로직까지 진행
                    UpdateServerStatusLabel("메타데이터는 동일하지만 이미지 변경이 감지되어 등록을 계속 진행합니다.", Color.Blue);
                }
                
                // 업데이트 시 isActive는 기본값 유지 (서버에서 처리)
            }

            // 현재 영화가 서버에 이미 존재하는 경우에는 PUT /movies/{id} 로 업데이트,
            // 그렇지 않으면 POST /movies 로 신규 등록한다.
            bool isUpdate = false;
            int serverMovieIdForUpdate = 0;
            var current = movieData.GetCurrentMovie();
            if (current != null)
            {
                string storedId = movieData.GetExtraAttribute(current.Id, ExtraKeyServerMovieId);
                if (!string.IsNullOrWhiteSpace(storedId) && int.TryParse(storedId, out int parsedId) && parsedId > 0)
                {
                    isUpdate = true;
                    serverMovieIdForUpdate = parsedId;
                }
            }

            string apiUrl = isUpdate
                ? ServerUrl.GetMovieUrl(dbInfo, serverMovieIdForUpdate)
                : ServerUrl.GetMoviesUrl(dbInfo);
            int timeoutSec = ServerUrl.GetRequestTimeoutSeconds(dbInfo);

            using (var httpClient = new HttpClient())
            {
                httpClient.Timeout = TimeSpan.FromSeconds(timeoutSec);

                try
                {
                    var serializer = new JavaScriptSerializer();
                    string json = serializer.Serialize(payload);
                    using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                    {
                        HttpResponseMessage response;
                        if (isUpdate)
                        {
                            response = await httpClient.PutAsync(apiUrl, content);
                        }
                        else
                        {
                            response = await httpClient.PostAsync(apiUrl, content);
                        }

                        string responseContent = await response.Content.ReadAsStringAsync();

                        if (!response.IsSuccessStatusCode)
                        {
                            string errorMessage = $"서버 응답 오류: {(int)response.StatusCode} {response.ReasonPhrase}";
                            try
                            {
                                var errorObj = serializer.Deserialize<Dictionary<string, object>>(responseContent);
                                if (errorObj != null)
                                {
                                    if (errorObj.TryGetValue("message", out object msg))
                                    {
                                        errorMessage = msg?.ToString() ?? errorMessage;
                                    }
                                    if (errorObj.TryGetValue("detail", out object detail))
                                    {
                                        errorMessage += $"\n상세: {detail}";
                                    }
                                    if (errorObj.TryGetValue("code", out object code))
                                    {
                                        errorMessage += $"\n오류 코드: {code}";
                                    }
                                }
                            }
                            catch
                            {
                                if (!string.IsNullOrWhiteSpace(responseContent))
                                {
                                    errorMessage += $"\n응답 내용: {responseContent}";
                                }
                            }

                            UpdateServerStatusLabel(errorMessage, Color.Red);
                            throw new Exception(errorMessage);
                        }
                        else
                        {
                            // 성공 시 서버에서 반환한 영화 ID를 파싱하여 로컬에 기록
                            int parsedMovieId = 0;
                            try
                            {
                                System.Diagnostics.Debug.WriteLine($"서버 응답 내용: {responseContent}");
                                var okObj = serializer.Deserialize<Dictionary<string, object>>(responseContent);
                                if (okObj != null)
                                {
                                    System.Diagnostics.Debug.WriteLine($"서버 응답 객체 키: {string.Join(", ", okObj.Keys)}");
                                    if (okObj.TryGetValue("movieId", out object midObj))
                                    {
                                        // createMovie 응답 { "movieId": 123 } 형식
                                        string midStr = midObj?.ToString();
                                        System.Diagnostics.Debug.WriteLine($"movieId 값: {midStr}");
                                        if (!int.TryParse(midStr, out parsedMovieId) || parsedMovieId <= 0)
                                        {
                                            string errorMsg = $"서버 응답에서 movieId 파싱 실패 또는 유효하지 않음: {midStr}";
                                            UpdateServerStatusLabel(errorMsg, Color.Orange);
                                            System.Diagnostics.Debug.WriteLine(errorMsg);
                                        }
                                        else
                                        {
                                            System.Diagnostics.Debug.WriteLine($"파싱된 movieId: {parsedMovieId}");
                                        }
                                    }
                                    else if (okObj.TryGetValue("id", out object idObj))
                                    {
                                        // 혹시 id 필드로 내려오는 경우까지 대비
                                        string idStr = idObj?.ToString();
                                        System.Diagnostics.Debug.WriteLine($"id 값: {idStr}");
                                        if (!int.TryParse(idStr, out parsedMovieId))
                                        {
                                            string errorMsg = $"서버 응답에서 id 파싱 실패: {idStr}";
                                            UpdateServerStatusLabel(errorMsg, Color.Orange);
                                            System.Diagnostics.Debug.WriteLine(errorMsg);
                                        }
                                        else
                                        {
                                            System.Diagnostics.Debug.WriteLine($"파싱된 id: {parsedMovieId}");
                                        }
                                    }
                                    else
                                    {
                                        // 응답 내용 로깅 (디버깅용)
                                        string errorMsg = $"서버 응답에 movieId 또는 id 필드가 없습니다. 응답: {responseContent}";
                                        UpdateServerStatusLabel(errorMsg, Color.Orange);
                                        System.Diagnostics.Debug.WriteLine(errorMsg);
                                    }
                                }
                                else
                                {
                                    string errorMsg = $"서버 응답 파싱 실패. 응답: {responseContent}";
                                    UpdateServerStatusLabel(errorMsg, Color.Orange);
                                    System.Diagnostics.Debug.WriteLine(errorMsg);
                                }
                            }
                            catch (Exception parseEx)
                            {
                                // 응답 파싱 실패는 치명적이지 않으므로 경고만 표시
                                string errorMsg = $"서버 응답 파싱 오류: {parseEx.Message}. 응답: {responseContent}";
                                UpdateServerStatusLabel(errorMsg, Color.Orange);
                                System.Diagnostics.Debug.WriteLine(errorMsg);
                                System.Diagnostics.Debug.WriteLine($"파싱 예외 스택: {parseEx.StackTrace}");
                            }

                            // 신규 등록(POST)인 경우, 서버에서 받은 ID를 로컬에 저장
                            if (!isUpdate && parsedMovieId > 0 && currentMovie != null)
                            {
                                movieData.SetExtraAttribute(currentMovie.Id, ExtraKeyServerMovieId, parsedMovieId.ToString());
                                currentServerMovieId = parsedMovieId;
                                if (lbl_movieIdOnServer != null)
                                {
                                    lbl_movieIdOnServer.Text = $"DB_Id( {parsedMovieId} )"; //parsedMovieId.ToString();
                                }
                            }
                            // 업데이트(PUT)인 경우에는 기존 storedId 를 다시 라벨에 반영
                            else if (isUpdate && serverMovieIdForUpdate > 0)
                            {
                                currentServerMovieId = serverMovieIdForUpdate;
                                if (lbl_movieIdOnServer != null)
                                {
                                    lbl_movieIdOnServer.Text = $"DB_Id( {serverMovieIdForUpdate} )"; //serverMovieIdForUpdate.ToString();
                                }
                            }

                            // 서버에 저장 후 스냅샷 업데이트
                            if (currentMovie != null)
                            {
                                var newSnapshot = new MovieData.ServerMetadataSnapshot
                                {
                                    Title = title,
                                    Studio = studio,
                                    ReleaseYear = ((int)nudReleaseYear.Value).ToString(),
                                    Director = director,
                                    Genre = genre,
                                    ManagementFolder = managementFolder,
                                    SnapshotAt = DateTime.Now
                                };
                                movieData.SetServerSnapshot(currentMovie.Id, newSnapshot);
                            }

                            // 이미지가 있으면 서버에 업로드
                            int serverMovieIdForImageUpload = 0;
                            if (isUpdate)
                            {
                                serverMovieIdForImageUpload = serverMovieIdForUpdate;
                            }
                            else
                            {
                                // 신규 등록인 경우 parsedMovieId 사용, 없으면 currentServerMovieId 사용
                                serverMovieIdForImageUpload = parsedMovieId > 0 ? parsedMovieId : (currentServerMovieId ?? 0);
                            }

                            // 이미지 업로드 시도
                            bool hasImagePath = !string.IsNullOrWhiteSpace(currentMovieImagePath);
                            bool imageFileExists = hasImagePath && File.Exists(currentMovieImagePath);
                            
                            // 디버깅 정보
                            string debugInfo = $"이미지 업로드 체크 - hasImagePath: {hasImagePath}, imageFileExists: {imageFileExists}, " +
                                $"serverMovieIdForImageUpload: {serverMovieIdForImageUpload}, parsedMovieId: {parsedMovieId}, " +
                                $"currentServerMovieId: {currentServerMovieId}, isUpdate: {isUpdate}";
                            System.Diagnostics.Debug.WriteLine(debugInfo);
                            // 서버 메시지 영역에도 이미지 업로드 조건을 표시
                            UpdateServerStatusLabel($"[이미지] {debugInfo}", Color.Gray);
                            
                            if (hasImagePath && imageFileExists)
                            {
                                if (serverMovieIdForImageUpload > 0)
                                {
                                    try
                                    {
                                        UpdateServerStatusLabel($"이미지 업로드 시작: {Path.GetFileName(currentMovieImagePath)}...", Color.Blue);
                                        await UploadImageToServer(serverMovieIdForImageUpload, currentMovieImagePath);
                                        UpdateServerStatusLabel($"이미지 업로드 완료: {Path.GetFileName(currentMovieImagePath)}", Color.Green);
                                    }
                                    catch (Exception imageEx)
                                    {
                                        // 이미지 업로드 실패는 경고만 표시하고 전체 등록은 성공으로 처리
                                        string errorMsg = $"영화 등록 완료, 이미지 업로드 실패: {imageEx.Message}";
                                        UpdateServerStatusLabel(errorMsg, Color.Orange);
                                        MessageBox.Show($"{errorMsg}\n\n상세 정보:\n{imageEx}", "이미지 업로드 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                        System.Diagnostics.Debug.WriteLine($"이미지 업로드 예외: {imageEx}");
                                    }
                                }
                                else
                                {
                                    // 서버 영화 ID를 얻지 못한 경우 경고
                                    string warningMsg = $"영화 등록 완료, 하지만 서버 영화 ID를 확인할 수 없어 이미지를 업로드하지 못했습니다.\n\n" +
                                        $"parsedMovieId: {parsedMovieId}, currentServerMovieId: {currentServerMovieId}, isUpdate: {isUpdate}";
                                    UpdateServerStatusLabel(warningMsg, Color.Orange);
                                    MessageBox.Show(warningMsg, "이미지 업로드 불가", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                    System.Diagnostics.Debug.WriteLine($"이미지 업로드 불가: {warningMsg}");
                                }
                            }
                            else if (hasImagePath && !imageFileExists)
                            {
                                // 이미지 경로는 있지만 파일이 없는 경우
                                string errorMsg = $"이미지 파일을 찾을 수 없습니다: {currentMovieImagePath}";
                                UpdateServerStatusLabel(errorMsg, Color.Orange);
                                MessageBox.Show(errorMsg, "이미지 파일 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                System.Diagnostics.Debug.WriteLine(errorMsg);
                            }
                            else if (!hasImagePath)
                            {
                                // 이미지 경로가 없는 경우 (정상 - 이미지가 선택되지 않음)
                                string infoMsg = "이미지 경로가 없습니다. 이미지 업로드를 건너뜁니다.";
                                System.Diagnostics.Debug.WriteLine(infoMsg);
                                UpdateServerStatusLabel($"[이미지] {infoMsg}", Color.Gray);
                            }
                        }
                    }
                }
                catch (TaskCanceledException ex)
                {
                    string errorMsg = $"서버 연결 시간 초과: {timeoutSec}초 내에 응답을 받지 못했습니다.\nURL: {apiUrl}";
                    UpdateServerStatusLabel(errorMsg, Color.Red);
                    throw new Exception(errorMsg, ex);
                }
                catch (HttpRequestException ex)
                {
                    string errorMsg = $"서버 연결 실패: {ex.Message}\n\n가능한 원인:\n1. 서버가 실행 중이지 않습니다\n2. 서버 주소나 포트가 올바르지 않습니다\n3. 방화벽이 연결을 차단하고 있습니다\n\nURL: {apiUrl}";
                    UpdateServerStatusLabel(errorMsg, Color.Red);
                    throw new Exception(errorMsg, ex);
                }
            }

            // 폼 내용을 초기화하지 않고 그대로 유지하여,
            // 사용자가 이어서 수정 작업을 할 수 있도록 한다.
            UpdateServerStatusLabel("영화 정보가 성공적으로 등록되었습니다.", Color.Green);
            MessageBox.Show("영화 정보가 성공적으로 등록되었습니다.", "등록 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);

            try
            {
                await LoadMoviesFromServer();
                UpdateServerStatusLabel("서버 연결됨 - 영화 목록 새로고침 완료", Color.Green, true);
            }
            catch (Exception ex)
            {
                UpdateServerStatusLabel($"새로고침 실패: {ex.Message}", Color.Orange);
                MessageBox.Show($"등록은 완료되었지만 목록 새로고침에 실패했습니다.\n{ex.Message}", "새로고침 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // 마지막으로 등록한 영화를 항상 현재 작업 영화로 저장하여,
            // 다음 프로그램 실행 시 변경된 폴더명이 복원되도록 한다.
            if (currentMovie != null)
            {
                movieData.SetCurrentMovie(currentMovie.Id);
            }

            // 등록 완료 후 변경 사항이 없으므로 등록 버튼 비활성화
            CheckMetadataChanges();
        }

        private void MainForm_ResizeEnd(object sender, EventArgs e)
        {
            // MainForm Location 을 기록한다.
            mainFormLocation = this.Location;
            List<string> pos = new List<string>() { mainFormLocation.X.ToString(), mainFormLocation.Y.ToString() };
            prof.WriteItemParams(PDN.S_SYSTEM, PDN.E_MAINFORM, PDN.I_LOCATION, pos);

            Size orgSize = configSize.get_orgFormSize();
            Size mWinSz = this.Size; // 현재 WinForm 크기
            bool size_controled = false;

            if (this.Size.Width < orgSize.Width)
            {
                size_controled = true;
                mWinSz.Width = orgSize.Width;
            }
            if (this.Size.Height < orgSize.Height)
            {
                size_controled = true;
                mWinSz.Height = orgSize.Height;
            }

            if (size_controled)
            {
                this.Size = mWinSz;
            }

            // dgv_MovieList의 폭과 높이를 SplitContainer의 Panel1 Client 영역에 맞춘다.
            var loc = dgv_MovieList.Location;
            dgv_MovieList.Width = Math.Max(0, splitContainer1.Panel1.ClientSize.Width - loc.X);
            dgv_MovieList.Height = Math.Max(0, splitContainer1.Panel1.ClientSize.Height - loc.Y);

            // panelContent의 폭과 높이를 SplitContainer의 Panel2 Client 영역에 맞추되,
            AdjustWorkPanelSize();

            prof.WriteItemParams(PDN.S_SYSTEM, PDN.E_MAINFORM, PDN.I_SIZE, new List<string> { mWinSz.Width.ToString(), mWinSz.Height.ToString() });
            prof.Write_DataToFile();
        }

        // panelContent의 폭과 높이를 SplitContainer의 Panel2 Client 영역에 맞추되,
        // 스크롤바가 충분히 보이도록 약간의 여유를 둔다.
        private void AdjustWorkPanelSize()
        {
            if (workTab != null && workTab.PnlWorking != null)
            {
                // this.tabPage2 -- 초기에 tabPage2의 Size가 tabControl1의 Client 영역에 맞지 않는 현상 해결
                Size size = tabControl1.ClientSize;
                var loc = tabPage2.Location;
                this.tabPage2.Width = size.Width - loc.X - 4;
                this.tabPage2.Height = size.Height - loc.Y - 4;
                // pnlWorking 
                Size client = this.tabPage2.ClientSize;
                var panel = workTab.PnlWorking;
                panel.Margin = Padding.Empty; 
                loc = panel.Location;

                int spaceX = 15; // 우측 여유
                int spaceY = 15; // 하단 여유 

                int newWidth = Math.Max(0, client.Width - loc.X - spaceX); // - panel.Margin.Right ;
                int newHeight = Math.Max(0, client.Height - loc.Y - spaceY); // - panel.Margin.Bottom - spaceY;

                panel.Width = newWidth;
                panel.Height = newHeight;
                //workTab.PnlWorking.Size = new Size(newWidth, newHeight);

                // 영화 제목 라벨
                //var lbl = workTab.lblCurrentMovieTitle;
                //loc = lbl.Location;
                //lbl.Height = 26;

                //
                var pnl = workTab.pnl_MovieDropArea;
                pnl.Location = new Point(pnl.Location.X, 20); // 테스트 20
                pnl.Height = 42;

                // 영화음원파일 버튼
                var btn = workTab.btnPickMovieFile;
                loc = btn.Location;
                btn.Height = 26; 
                var txt = workTab.lblMovieFile;
                txt.Location = new Point(txt.Location.X, btn.Location.Y);
                txt.Height = 26; 
            }
        }

        private void picMovieImage_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void picMovieImage_DragDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    string imagePath = files[0];
                    LoadMovieImage(imagePath);
                }
            }
        }

        private void btnPickMovieImage_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "이미지 파일|*.jpg;*.jpeg;*.png;*.bmp;*.gif|모든 파일|*.*";
                openFileDialog.FilterIndex = 1;
                openFileDialog.RestoreDirectory = true;

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    LoadMovieImage(openFileDialog.FileName);
                }
            }
        }

        private void LoadMovieImage(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                return;
            }

            try
            {
                // 이미지 파일 확장자 확인
                string extension = Path.GetExtension(imagePath).ToLower();
                string[] supportedExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif" };
                
                if (!supportedExtensions.Contains(extension))
                {
                    MessageBox.Show("지원하는 이미지 형식이 아닙니다.\n지원 형식: JPG, JPEG, PNG, BMP, GIF", 
                        "이미지 형식 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // 현재 작업 영화 확인
                var currentMovie = movieData.GetCurrentMovie();
                if (currentMovie == null)
                {
                    MessageBox.Show("먼저 영화 정보를 등록하거나 선택해주세요.", "영화 정보 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // 현재 작업 폴더의 images 폴더로 이미지 복사
                string movieFolder = movieData.GetMovieFolderPath(currentMovie.Id);
                string imagesFolder = Path.Combine(movieFolder, "images");
                Directory.CreateDirectory(imagesFolder);

                string fileName = Path.GetFileName(imagePath);
                string sanitizedFileName = MovieData.SanitizeFileName(fileName);
                string targetPath = Path.Combine(imagesFolder, sanitizedFileName);

                // 중복 파일명 처리
                int counter = 1;
                string baseFileName = Path.GetFileNameWithoutExtension(sanitizedFileName);
                string fileExtension = Path.GetExtension(sanitizedFileName);
                while (File.Exists(targetPath))
                {
                    sanitizedFileName = $"{baseFileName}_{counter++}{fileExtension}";
                    targetPath = Path.Combine(imagesFolder, sanitizedFileName);
                }

                // 이미지 파일 복사
                File.Copy(imagePath, targetPath, true);

                // 이미지 로드
                using (var fs = new FileStream(targetPath, FileMode.Open, FileAccess.Read))
                {
                    var originalImage = Image.FromStream(fs);
                    
                    // PictureBox에 이미지 설정
                    if (picMovieImage.Image != null)
                    {
                        picMovieImage.Image.Dispose();
                    }
                    
                    picMovieImage.Image = new Bitmap(originalImage);
                    currentMovieImagePath = targetPath; // 복사된 경로로 설정
                    
                    // btnPickMovieImage 활성화
                    if (btnPickMovieImage != null)
                    {
                        btnPickMovieImage.Enabled = true;
                    }

                    // 변경 감지하여 등록 버튼 활성화
                    CheckMetadataChanges();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"이미지를 로드하는 중 오류가 발생했습니다.\n{ex.Message}", 
                    "이미지 로드 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void lvAudioViewFiles_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            // 헤더 배경색을 가벼운 하늘색으로 설정
            Color skyBlue = Color.FromArgb(173, 216, 230); // 가벼운 하늘색
            using (SolidBrush backBrush = new SolidBrush(skyBlue))
            {
                e.Graphics.FillRectangle(backBrush, e.Bounds);
            }

            // 헤더 텍스트 그리기
            TextRenderer.DrawText(e.Graphics, e.Header.Text, e.Font, e.Bounds, Color.Black,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void lvAudioViewFiles_DrawItem(object sender, DrawListViewItemEventArgs e)
        {
            // Details 뷰에서는 개별 셀을 DrawSubItem에서 모두 그리므로 여기서는 아무 것도 하지 않는다.
            if (e.Item.ListView.View != View.Details)
            {
                e.DrawBackground();
                e.DrawText();
            }
        }

        private void lvAudioViewFiles_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            // 기본 배경 (선택 상태 반영)
            Color backColor = e.Item.Selected ? SystemColors.Highlight : e.SubItem.BackColor;
            using (var backBrush = new SolidBrush(backColor))
            {
                e.Graphics.FillRectangle(backBrush, e.Bounds);
            }

            // "등록" 컬럼(인덱스 3)은 버튼처럼 커스텀 렌더링
            if (e.ColumnIndex == 3)
            {
                Rectangle bounds = e.Bounds;

                // 약간의 마진을 주어 버튼처럼 보이도록 영역 축소
                int margin = 2;
                bounds.Inflate(-margin, -margin);

                bool hasRegisterText =
                    !string.IsNullOrWhiteSpace(e.SubItem.Text) &&
                    string.Equals(e.SubItem.Text, "등록", StringComparison.OrdinalIgnoreCase);

                // 버튼 배경색
                Color buttonBack = hasRegisterText ? Color.BlueViolet : SystemColors.Control;

                using (SolidBrush backBrush = new SolidBrush(buttonBack))
                using (Pen borderPen = new Pen(Color.SteelBlue))
                {
                    e.Graphics.FillRectangle(backBrush, bounds);
                    e.Graphics.DrawRectangle(borderPen, bounds);
                }

                // 텍스트 렌더링 (가운데 정렬)
                if (hasRegisterText)
                {
                    TextRenderer.DrawText(
                        e.Graphics,
                        e.SubItem.Text,
                        e.SubItem.Font ?? e.Item.ListView.Font,
                        bounds,
                        Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                }
            }
            else
            {
                // 일반 컬럼: 텍스트만 그리기
                Color foreColor = e.Item.Selected ? SystemColors.HighlightText : e.SubItem.ForeColor;
                TextRenderer.DrawText(
                    e.Graphics,
                    e.SubItem.Text,
                    e.SubItem.Font ?? e.Item.ListView.Font,
                    e.Bounds,
                    foreColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        /// <summary>
        /// 메타데이터 변경 여부를 확인하고 등록 버튼 상태를 업데이트
        /// </summary>
        private void CheckMetadataChanges()
        {
            if (btnRegisterMovie == null)
            {
                return;
            }

            var currentMovie = movieData.GetCurrentMovie();
            if (currentMovie == null)
            {
                btnRegisterMovie.Enabled = false;
                btnRegisterMovie.Text = "등록";
                return;
            }

            // 서버 연결 상태 확인
            bool isServerConnected = ServerUrl.CheckServerConnectionUrl(dbInfo);

            // 서버 스냅샷 확인
            var snapshot = movieData.GetServerSnapshot(currentMovie.Id);
            bool hasChanges = false;

            if (snapshot != null)
            {
                // 필드별 변경 감지
                string currentTitle = txtMovieTitle?.Text?.Trim() ?? string.Empty;
                string currentStudio = txtStudioName?.Text?.Trim() ?? string.Empty;
                string currentDirector = txtDirector?.Text?.Trim() ?? string.Empty;
                string currentGenre = cmbGenre?.SelectedIndex >= 0 ? cmbGenre.SelectedItem?.ToString() ?? string.Empty : string.Empty;
                string currentYear = nudReleaseYear?.Value != null ? ((int)nudReleaseYear.Value).ToString() : string.Empty;
                string currentFolder = txtMovieFolderName?.Text?.Trim() ?? string.Empty;

                hasChanges = 
                    !string.Equals(currentTitle, snapshot.Title ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(currentStudio, snapshot.Studio ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(currentDirector, snapshot.Director ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(currentGenre, snapshot.Genre ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(currentYear, snapshot.ReleaseYear ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(currentFolder, snapshot.ManagementFolder ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                // 스냅샷이 없으면 항상 변경 가능 상태
                hasChanges = true;
            }

            // 이미지 변경 여부 확인 (서버 기준 이미지와 비교)
            if (!hasChanges)
            {
                try
                {
                    // 서버에서 받은 기준 이미지가 있고 체크섬이 있으면, 현재 이미지와 체크섬 비교
                    if (!string.IsNullOrWhiteSpace(serverImageChecksum))
                    {
                        string currentImageChecksum = null;
                        if (!string.IsNullOrWhiteSpace(currentMovieImagePath) && File.Exists(currentMovieImagePath))
                        {
                            currentImageChecksum = CalculateFileChecksum(currentMovieImagePath);
                        }

                        // 둘 다 null/빈 값이 아니고, 체크섬이 다르면 이미지가 변경된 것으로 판단
                        if (!string.IsNullOrWhiteSpace(currentImageChecksum) &&
                            !string.Equals(currentImageChecksum, serverImageChecksum, StringComparison.OrdinalIgnoreCase))
                        {
                            hasChanges = true;
                        }
                    }
                    else
                    {
                        // 서버 기준 이미지 정보가 없는데 로컬에 이미지가 있는 경우도 변경으로 간주
                        if (!string.IsNullOrWhiteSpace(currentMovieImagePath) && File.Exists(currentMovieImagePath))
                        {
                            hasChanges = true;
                        }
                    }
                }
                catch
                {
                    // 체크섬 비교 중 오류가 발생해도 등록 버튼이 완전히 막히지 않도록 함
                }
            }

            // 버튼 상태 업데이트
            btnRegisterMovie.Enabled = hasChanges;
            if (isServerConnected)
            {
                btnRegisterMovie.Text = hasChanges ? "등록" : "등록";
            }
            else
            {
                btnRegisterMovie.Text = hasChanges ? "저장" : "저장";
            }
        }

        /// <summary>
        /// 서버 URL 확인
        /// </summary>

        /// <summary>
        /// 서버 파일 체크섬 조회 (ExtraAttributes에서)
        /// </summary>
        private string GetServerFileChecksum(AudioViewFileCategory category)
        {
            var currentMovie = movieData?.GetCurrentMovie();
            if (currentMovie == null)
            {
                return null;
            }

            string key = $"{GetAudioViewAttributeKey(category)}.Checksum";
            return movieData.GetExtraAttribute(currentMovie.Id, key);
        }

        /// <summary>
        /// 서버 파일 체크섬 저장
        /// </summary>
        private void SetServerFileChecksum(AudioViewFileCategory category, string checksum)
        {
            var currentMovie = movieData.GetCurrentMovie();
            if (currentMovie == null)
            {
                return;
            }

            string key = $"{GetAudioViewAttributeKey(category)}.Checksum";
            movieData.SetExtraAttribute(currentMovie.Id, key, checksum);
        }

        /// <summary>
        /// 파일 체크섬 계산 (SHA256)
        /// </summary>
        private string CalculateFileChecksum(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return null;
            }

            try
            {
                using (var sha256 = System.Security.Cryptography.SHA256.Create())
                {
                    using (var stream = File.OpenRead(filePath))
                    {
                        byte[] hash = sha256.ComputeHash(stream);
                        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private sealed class AudioFileComboItem
        {
            public AudioFileComboItem(string movieId, MovieData.MovieWorkFile file, string fullPath)
            {
                MovieId = movieId;
                FileName = file.FileName;
                OriginalFileName = file.OriginalFileName;
                FullPath = fullPath;
                FileSize = file.FileSize;
                Description = file.Description;
            }

            public string MovieId { get; }
            public string FileName { get; }
            public string OriginalFileName { get; }
            public string FullPath { get; }
            public long FileSize { get; }
            public string Description { get; }

            public string DisplayName => $"{FileName} ({FormatSize(FileSize)})";

            public override string ToString()
            {
                return DisplayName;
            }

            private static string FormatSize(long bytes)
            {
                if (bytes <= 0)
                {
                    return "0 B";
                }

                string[] units = { "B", "KB", "MB", "GB", "TB" };
                double size = bytes;
                int index = 0;
                while (size >= 1024 && index < units.Length - 1)
                {
                    size /= 1024;
                    index++;
                }

                return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", size, units[index]);
            }
        }

        /// <summary>
        /// 서버 연결 재시도 버튼 클릭 이벤트
        /// </summary>
        private async void BtnRetryServerConnection_Click(object sender, EventArgs e)
        {
            try
            {
                UpdateServerStatusLabel("서버 연결 재시도 중...", Color.Blue);
                await CheckServerConnection();
                // 서버에서 영화목록을 받아온다. -- added 2025.12.03
                await LoadMoviesFromServer();
            }
            catch (Exception ex)
            {
                UpdateServerStatusLabel($"서버 연결 재시도 실패: {ex.Message}", Color.Red);
            }
        }

        /// <summary>
        /// 선택된 영화 항목 가져오기 버튼 클릭 이벤트
        /// </summary>
        private async void BtnLoadSelectedMovie_Click(object sender, EventArgs e)
        {
            if (dgv_MovieList == null || dgv_MovieList.SelectedRows.Count == 0)
            {
                MessageBox.Show("가져올 영화를 선택해주세요.", "선택 없음", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DataGridViewRow selectedRow = dgv_MovieList.SelectedRows[0];
            if (selectedRow == null || selectedRow.Cells.Count == 0)
            {
                MessageBox.Show("선택한 항목이 유효하지 않습니다.", "유효하지 않은 선택", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            object idValue = selectedRow.Cells[0].Value;
            if (idValue == null)
            {
                MessageBox.Show("선택한 항목에는 서버 ID가 없습니다.", "서버 ID 없음", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!int.TryParse(Convert.ToString(idValue), out int serverMovieId) || serverMovieId <= 0)
            {
                MessageBox.Show("서버 ID 형식이 올바르지 않습니다.", "서버 ID 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 로컬에 작업 중인 영화가 있는지 확인
            var currentMovie = movieData.GetCurrentMovie();
            if (currentMovie != null)
            {
                // 선택한 서버 영화 정보 가져오기
                string serverTitle = SafeGetCellText(selectedRow, 1);
                if (string.IsNullOrWhiteSpace(serverTitle))
                {
                    serverTitle = $"Movie_{serverMovieId}";
                }

                // 현재 작업 중인 영화 제목
                string currentTitle = !string.IsNullOrWhiteSpace(currentMovie.Title) ? currentMovie.Title : "제목 없음";

                // 확인 메시지
                string message = $"현재 작업 중인 영화가 있습니다.\n\n" +
                    $"현재 작업 영화: {currentTitle}\n" +
                    $"선택한 서버 영화: {serverTitle}\n\n" +
                    $"서버 영화를 가져오면 새로운 영화로 전환되며,\n" +
                    $"모든 작업 내용이 서버 영화 기준으로 변경됩니다.\n" +
                    $"계속 진행하시겠습니까?";
                
                DialogResult result = MessageBox.Show(message, "작업 중인 영화 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if (result != DialogResult.Yes)
                {
                    return; // 사용자가 취소한 경우
                }
            }

            try
            {
                await LoadServerMovieToLocalAsync(serverMovieId, selectedRow);
                UpdateServerStatusLabel($"영화 '{SafeGetCellText(selectedRow, 1)}' 가져오기 완료", Color.Green);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"서버 영화 정보를 로컬로 가져오는 중 오류가 발생했습니다.\n{ex.Message}",
                    "서버 로드 오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateServerStatusLabel($"영화 가져오기 실패: {ex.Message}", Color.Red);
            }
        }

        /// <summary>
        /// 선택된 영화 항목 삭제 버튼 클릭 이벤트
        /// </summary>
        private async void BtnDeleteSelectedMovie_Click(object sender, EventArgs e)
        {
            if (dgv_MovieList == null || dgv_MovieList.SelectedRows.Count == 0)
            {
                MessageBox.Show("삭제할 영화를 선택해주세요.", "선택 없음", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DataGridViewRow selectedRow = dgv_MovieList.SelectedRows[0];
            if (selectedRow == null || selectedRow.Cells.Count == 0)
            {
                MessageBox.Show("선택한 항목이 유효하지 않습니다.", "유효하지 않은 선택", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            object idValue = selectedRow.Cells[0].Value;
            if (idValue == null)
            {
                MessageBox.Show("선택한 항목에는 서버 ID가 없습니다. 서버에 등록되지 않은 영화는 삭제할 수 없습니다.", 
                    "서버 ID 없음", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!int.TryParse(Convert.ToString(idValue), out int serverMovieId) || serverMovieId <= 0)
            {
                MessageBox.Show("서버 ID 형식이 올바르지 않습니다.", "서버 ID 오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 삭제 확인
            string movieTitle = SafeGetCellText(selectedRow, 1);
            if (string.IsNullOrWhiteSpace(movieTitle))
            {
                movieTitle = $"Movie_{serverMovieId}";
            }

            string message = $"다음 영화를 서버에서 삭제하시겠습니까?\n\n" +
                $"영화 제목: {movieTitle}\n" +
                $"서버 ID: {serverMovieId}\n\n" +
                $"이 작업은 되돌릴 수 없습니다.";
            
            DialogResult result = MessageBox.Show(message, "영화 삭제 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (result != DialogResult.Yes)
            {
                return; // 사용자가 취소한 경우
            }

            int timeoutSec = ServerUrl.GetRequestTimeoutSeconds(dbInfo);
            
            try
            {
                string apiUrl = ServerUrl.GetMovieUrl(dbInfo, serverMovieId);

                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(timeoutSec);
                    
                    var response = await httpClient.DeleteAsync(apiUrl);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        // 목록에서 행 제거
                        dgv_MovieList.Rows.Remove(selectedRow);
                        
                        // 목록 상태 업데이트
                        UpdateMovieListStatus(null);
                        
                        UpdateServerStatusLabel($"영화 '{movieTitle}' 삭제 완료", Color.Green);
                        MessageBox.Show($"영화 '{movieTitle}'가 서버에서 삭제되었습니다.", 
                            "삭제 완료", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        string errorMsg = $"서버 삭제 실패: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                        UpdateServerStatusLabel(errorMsg, Color.Red);
                        MessageBox.Show(errorMsg, "삭제 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
            catch (TaskCanceledException)
            {
                string errorMsg = $"서버 연결 시간 초과: {timeoutSec}초 내에 응답을 받지 못했습니다.";
                UpdateServerStatusLabel(errorMsg, Color.Red);
                MessageBox.Show(errorMsg, "삭제 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (HttpRequestException ex)
            {
                string errorMsg = $"서버 연결 실패: {ex.Message}\n\n가능한 원인:\n1. 서버가 실행 중이지 않습니다\n2. 서버 주소나 포트가 올바르지 않습니다\n3. 방화벽이 연결을 차단하고 있습니다";
                UpdateServerStatusLabel(errorMsg, Color.Red);
                MessageBox.Show(errorMsg, "삭제 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex)
            {
                string errorMsg = $"영화 삭제 중 오류 발생: {ex.Message}";
                UpdateServerStatusLabel(errorMsg, Color.Red);
                MessageBox.Show(errorMsg, "삭제 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 버튼에 아이콘 이미지를 설정합니다.
        /// </summary>
        private void SetupButtonIcons()
        {
            if (btnRetryServerConnection != null)
            {
                btnRetryServerConnection.Image = BitmapIcons.CreateRetryIcon();
                btnRetryServerConnection.Text = "";
                btnRetryServerConnection.ImageAlign = ContentAlignment.MiddleCenter;
                btnRetryServerConnection.TextImageRelation = TextImageRelation.ImageBeforeText;
            }

            if (btnLoadSelectedMovie != null)
            {
                btnLoadSelectedMovie.Image = BitmapIcons.CreateDownloadIcon();
                btnLoadSelectedMovie.Text = "";
                btnLoadSelectedMovie.ImageAlign = ContentAlignment.MiddleCenter;
                btnLoadSelectedMovie.TextImageRelation = TextImageRelation.ImageBeforeText;
            }

            if (btnDeleteSelectedMovie != null)
            {
                btnDeleteSelectedMovie.Image = BitmapIcons.CreateDeleteIcon();
                btnDeleteSelectedMovie.Text = "";
                btnDeleteSelectedMovie.ImageAlign = ContentAlignment.MiddleCenter;
                btnDeleteSelectedMovie.TextImageRelation = TextImageRelation.ImageBeforeText;
            }
        }


    }
}

