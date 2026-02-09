using AudioViewStudio.Analysis;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudioViewStudio
{
    public class AudioFptFileInfo
    {
        public string svrMovieID = "미등록";
        public string movieFolder = string.Empty;
        public string mvAudioFile = string.Empty;
        public string featureDir = string.Empty;
        public string featureFile = string.Empty;
        public string extention = ".mpack";
        public int fftSize = 2048;
        public int hopSize = 1024;
        public bool mvHashOnly = true;
        public bool mvRvsIndex = true; // 역인덱스 생셩 여부 

        public AudioFptFileInfo() { }

        public AudioFptFileInfo Copy()
        {
            AudioFptFileInfo copy = new AudioFptFileInfo();
            copy.svrMovieID = this.svrMovieID;
            copy.movieFolder = this.movieFolder;
            copy.mvAudioFile = this.mvAudioFile;
            copy.featureDir = this.featureDir;
            copy.featureFile = this.featureFile;
            copy.extention = this.extention;
            copy.fftSize = this.fftSize;
            copy.hopSize = this.hopSize;
            copy.mvHashOnly = this.mvHashOnly;
            return copy;
        }
        public bool Equals(AudioFptFileInfo other)
        {
            return svrMovieID == other.svrMovieID &&
                movieFolder == other.movieFolder &&
                mvAudioFile == other.mvAudioFile &&
                featureDir == other.featureDir &&
                featureFile == other.featureFile &&
                extention == other.extention &&
                fftSize == other.fftSize &&
                hopSize == other.hopSize &&
                mvHashOnly == other.mvHashOnly &&
                mvRvsIndex == other.mvRvsIndex;
        }
        public void Update(AudioFptFileInfo other)
        {
            svrMovieID = other.svrMovieID;
            movieFolder = other.movieFolder;
            mvAudioFile = other.mvAudioFile;
            featureDir = other.featureDir;
            featureFile = other.featureFile;
            extention = other.extention;
            fftSize = other.fftSize;
            hopSize = other.hopSize;
            mvHashOnly = other.mvHashOnly;
            mvRvsIndex = other.mvRvsIndex;
        }

        public string FingerprintFolder()
        {
            return System.IO.Path.Combine(movieFolder, "fingerprint");
        }
        // 핑거프린트 파일명과 확장자 반환
        public (string fileType, string extention) GetFingerprintFileType()
        {
            string modeSuffix = mvHashOnly ? "hash" : "full";
            return ($"_fingerprint_{fftSize}_{hopSize}_{modeSuffix}{extention}", extention);
        }
        public string MovieFptFNameByValues(int FFTSize, int HopSize)
        {
            string modeSuffix = mvHashOnly ? "hash" : "full";
            string Tail = string.Format($"_fingerprint_{FFTSize}_{HopSize}_{modeSuffix}{extention}", extention);
            return Tail;
        }
        public string MovieFptFNameTail()
        {
            string modeSuffix = mvHashOnly ? "hash" : "full";
            string Tail = string.Format($"_fingerprint_{fftSize}_{hopSize}_{modeSuffix}{extention}", extention);
            return Tail;
        }
        /// <summary>
        /// 파일을 찾는다.
        /// (bool success, string filePath) FindFile(string fptFolder, string keyWord, string fileType, string extention)
        /// </summary>
        /// <param name="fptFolder"></param>
        /// <param name="keyWord"></param>
        /// <param name="fileType"></param>
        /// <param name="extention"></param>
        /// <returns></returns>
        public (bool success, string filePath) FindFile(string fptFolder, string keyWord, string fileType, string extention)
        {
            string[] files = Directory.GetFiles(fptFolder, "*" + fileType);

            // 정확한 패턴으로 찾지 못한 경우, 더 유연한 검색 시도
            if (files.Length == 0)
            {
                // 모든 .mpack 파일 검색
                string[] allMpackFiles = Directory.GetFiles(fptFolder, "*" + extention);
                if (allMpackFiles.Length > 0)
                {
                    // fingerprint가 포함된 파일만 필터링
                    files = allMpackFiles.Where(f => Path.GetFileName(f).Contains(keyWord)).ToArray();

                    // 여전히 찾지 못한 경우, 가장 최근 파일 사용
                    if (files.Length == 0 && allMpackFiles.Length > 0)
                    {
                        // 가장 최근 수정된 파일 선택
                        files = new[] { allMpackFiles.OrderByDescending(f => new FileInfo(f).LastWriteTime).First() };
                    }
                }
            }

            if (files.Length == 0)
            {
                // 디버깅 정보 포함
                string[] allFiles = Directory.GetFiles(fptFolder);
                string fileList = (allFiles.Length > 0) ? string.Join("\n", allFiles.Select(f => $"  - {Path.GetFileName(f)}")) : "  (파일 없음)";
                return (false, $"폴더 {fptFolder} 에 파일이 없습니다.\n\n" + $"검색 패턴: {fileType}\n\n" + $"폴더 내 파일 목록:\n{fileList}");
            }
            // 첫 번째 핑거프린트 파일 사용 (또는 가장 최근 파일 선택 가능)
            string fingerprintFile = Path.GetFileName(files[0]);
            string fpFilePath = Path.Combine(fptFolder, fingerprintFile);
            if (!File.Exists(fpFilePath))
            {
                return (false, $"핑거프린트 파일 {fpFilePath} 가 존재하지 않습니다.");
            }
            return (true, fpFilePath);
        }

        public (bool found, string fptFilePath) Find_movieFptFileAdapted()
        {
            string folder = FingerprintFolder();
            var (fileType, ext) = GetFingerprintFileType(); // FFTSzie, HopSize 변경된 기준으로 찾는다.
            return FindFile(folder, "_fingerprint_", fileType, ext);
        }
        public string GetAudioFilePath()
        {
            return System.IO.Path.Combine(movieFolder, mvAudioFile);
        }
        public string GetFeatureFilePath()
        {
            return System.IO.Path.Combine(featureDir, featureFile);
        }
    }

    /// <summary>
    /// 통일된 Peak 검출 파라미터
    /// </summary>
    public class FingerprintConfig
    {
        // Peak 검출 파라미터 (원본/실시간 동일하게 적용)
        public int FFTSize = 4096;
        public int HopSize = 2048;
        public bool mvHashOnly = true; // 영화 해시 전용 여부
        public bool mvRvsIndex = true; // 영화 역인덱스 생성 여부
        public int PeakNeighborhoodSize = 5;
        public int MaxPeaksPerFrame = 5;
        public double PeakThresholdMultiplier = 2.0;  // 평균 + 2*표준편차

        // 해시 생성 파라미터
        public double TimeQuantizationUnit = 0.2;     // 0.2초 단위
        public double FrequencyQuantizationUnit = 200; // 200Hz 단위
        public int HashTimeWindow = 3;                // 3초 윈도우

        public void Update(FingerprintConfig o)
        {
            // Peak 검출 파라미터 (원본/실시간 동일하게 적용)
            FFTSize = o.FFTSize;
            HopSize = o.HopSize;
            mvHashOnly = o.mvHashOnly;
            mvRvsIndex = o.mvRvsIndex;
            PeakNeighborhoodSize = o.PeakNeighborhoodSize;
            MaxPeaksPerFrame = o.MaxPeaksPerFrame;
            PeakThresholdMultiplier = o.PeakThresholdMultiplier;  // 평균 + 2*표준편차
            // 해시 생성 파라미터
            TimeQuantizationUnit = o.TimeQuantizationUnit;     // 0.2초 단위
            FrequencyQuantizationUnit = o.FrequencyQuantizationUnit; // 200Hz 단위
            HashTimeWindow = o.HashTimeWindow;                // 3초 윈도우
        }
        public bool Equals(FingerprintConfig other)
        {
            return FFTSize == other.FFTSize &&
                HopSize == other.HopSize &&
                mvHashOnly == other.mvHashOnly &&
                mvRvsIndex == other.mvRvsIndex &&
                PeakNeighborhoodSize == other.PeakNeighborhoodSize &&
                MaxPeaksPerFrame == other.MaxPeaksPerFrame &&
                PeakThresholdMultiplier == other.PeakThresholdMultiplier &&
                TimeQuantizationUnit == other.TimeQuantizationUnit &&
                FrequencyQuantizationUnit == other.FrequencyQuantizationUnit &&
                HashTimeWindow == other.HashTimeWindow;
        }

        public string ToStr()
        {
            return string.Format("FFT:{0}, HOP:{1}, MV_HO:{2}, MV_RI:{3}, PNS:{4}, MPPF:{5}, PTM:{6}, TQU:{7}, FQU:{8}, HTW:{9}",
                FFTSize, HopSize, mvHashOnly, mvRvsIndex, PeakNeighborhoodSize, MaxPeaksPerFrame, PeakThresholdMultiplier,
                TimeQuantizationUnit, FrequencyQuantizationUnit, HashTimeWindow);
        }

    }

    public class PreprocessParam
    {
        public double hpCutoffHz = 80.0;
        public double baseGateMultiplier = 2.2;
        public double attackMs = 5.0;
        public double releaseMs = 80.0;
        public double targetRms = 0.12;
        public double clipDrive = 1.6;
        public void Update(PreprocessParam pp)
        {
            hpCutoffHz = pp.hpCutoffHz;
            baseGateMultiplier = pp.baseGateMultiplier;
            attackMs = pp.attackMs;
            releaseMs = pp.releaseMs;
            targetRms = pp.targetRms;
            clipDrive = pp.clipDrive;
        }
        public PreprocessParam Clone()
        {
            return new PreprocessParam
            {
                hpCutoffHz = this.hpCutoffHz,
                baseGateMultiplier = this.baseGateMultiplier,
                attackMs = this.attackMs,
                releaseMs = this.releaseMs,
                targetRms = this.targetRms,
                clipDrive = this.clipDrive
            };
        }
        public bool Equals(PreprocessParam pp)
        {
            return hpCutoffHz == pp.hpCutoffHz &&
                baseGateMultiplier == pp.baseGateMultiplier &&
                attackMs == pp.attackMs &&
                releaseMs == pp.releaseMs &&
                targetRms == pp.targetRms &&
                clipDrive == pp.clipDrive;
        }
        public string ToStr()
        {
            return string.Format("HPF:{0}, BG_MUL:{1}, ATK_MS:{2}, REL_MS:{3}, TRMS:{4}, CLIP_DV:{5}",
                hpCutoffHz, baseGateMultiplier, attackMs, releaseMs, targetRms, clipDrive);
        }
    }

    /// <summary>
    /// Root Class for Audio Fingerprint Picking Parameters
    /// </summary>
    public class PickAudioFpParam
    {
        // 오디오 처리 파라미터
        public int mvCategoryId = 0; // 0(음악), 1(영화/오디오), 2(대사/효과음)
        public int sampleRate = 44100;
        public FingerprintConfig fptCfg = new FingerprintConfig();

        // 오프셋 집중도 기반 동적 확장/게이트 조정 파라미터
        public double offsetConcntThreshold = 0.3; // 이 값 미만이면 보강 동작
        public int maxWindowSizeMs = 10000; // 일반 최대 구간
        public int maxWindowSizeMsLowOffset = 15000; // 집중도 낮을 때 최대 구간
        public double gateSoftnessMultiplier = 1.0; // 1.0 기본, 클수록 게이트가 부드러움
        public double gateSoftnessMultiplierLowOffset = 4.0; //2.0; // 집중도 낮을 때 게이트 완화
        // Audio Peak 추출
        public bool adaptiveDynamic = true;// 동적 확장/게이트 조정 사용 여부
        public TimeSpan pickTime = TimeSpan.Zero;
        // 품질기반 필터링 사용 여부
        public bool UseQualityBasedFiltering = true; // 품질기반 필터링 사용 여부
        public double QualityThreshold = 60.0; // 최소 허용 품질 점수 (0-100)
        public double minMagnitude = 0.0;      // 최소 크기 제한 (노이즈 제거)
        public int targetHashesPerSec = 0;     // 목표 해시 밀도 (0이면 미사용)
        // 전처리 파라미터 (UI 조절)
        public PreprocessParam PP = new PreprocessParam();

        public PickAudioFpParam() { }

        public void Update(PickAudioFpParam other)
        {
            //fptFile.Update(other.fptFile);
            mvCategoryId = other.mvCategoryId;
            sampleRate = other.sampleRate;
            fptCfg.Update(other.fptCfg);
            offsetConcntThreshold = other.offsetConcntThreshold;
            maxWindowSizeMs = other.maxWindowSizeMs;
            maxWindowSizeMsLowOffset = other.maxWindowSizeMsLowOffset;
            gateSoftnessMultiplier = other.gateSoftnessMultiplier;
            gateSoftnessMultiplierLowOffset = other.gateSoftnessMultiplierLowOffset;
            adaptiveDynamic = other.adaptiveDynamic;
            pickTime = other.pickTime;
            UseQualityBasedFiltering = other.UseQualityBasedFiltering;
            QualityThreshold = other.QualityThreshold;
            PP.Update(other.PP);
        }
        public bool IsChanged(PickAudioFpParam other)
        {
            return !Equals(other);
        }
        public bool Equals(PickAudioFpParam other)
        {
            return //fptFile.Equals(other.fptFile) && 
                mvCategoryId == other.mvCategoryId &&
                sampleRate == other.sampleRate &&
                fptCfg.Equals(other.fptCfg) &&
                offsetConcntThreshold == other.offsetConcntThreshold &&
                maxWindowSizeMs == other.maxWindowSizeMs &&
                maxWindowSizeMsLowOffset == other.maxWindowSizeMsLowOffset &&
                gateSoftnessMultiplier == other.gateSoftnessMultiplier &&
                gateSoftnessMultiplierLowOffset == other.gateSoftnessMultiplierLowOffset &&
                adaptiveDynamic == other.adaptiveDynamic &&
                pickTime == other.pickTime &&
                UseQualityBasedFiltering == other.UseQualityBasedFiltering &&
                QualityThreshold == other.QualityThreshold &&
                PP.Equals(other.PP);
        }
        public string ParamsToString()
        {
            string prep = PP.ToStr();
            string config = fptCfg.ToStr();
            return string.Format("CATEG:{0}, SR:{1}, {2}, OC_THR:{3}, MAX_WIN_MS:{4}, MAX_WIN_MS_LO:{5}, GATE_SM:{6}, GATE_SM_LO:{7}, ADAPT:{8}, PT:{9}, U_QBF:{10}, Q_THR:{11}, {12}",
                mvCategoryId, sampleRate, config, offsetConcntThreshold, maxWindowSizeMs, maxWindowSizeMsLowOffset,
                gateSoftnessMultiplier, gateSoftnessMultiplierLowOffset, adaptiveDynamic, pickTime, UseQualityBasedFiltering, QualityThreshold,  prep);
        }
        public bool SetParamsFromString(string s)
        {
            try
            {
                var parts = s.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var kv = part.Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
                    if (kv.Length == 4) { pickTime = TimeSpan.Parse(part.Substring(kv[0].Length + 1));  continue; }
                    if (kv.Length != 2) continue;
                    var key = kv[0].Trim();
                    var value = kv[1].Trim();
                    switch (key)
                    {
                        case "CATEG": mvCategoryId = int.Parse(value); break;
                        case "SR": sampleRate = int.Parse(value); break;
                        case "FFT": fptCfg.FFTSize = int.Parse(value); break;
                        case "HOP": fptCfg.HopSize = int.Parse(value); break;
                        case "MV_HO": fptCfg.mvHashOnly = bool.Parse(value); break;
                        case "MV_RI": fptCfg.mvRvsIndex = bool.Parse(value); break;
                        case "OC_THR": offsetConcntThreshold = double.Parse(value); break;
                        case "MAX_WIN_MS": maxWindowSizeMs = int.Parse(value); break;
                        case "MAX_WIN_MS_LO": maxWindowSizeMsLowOffset = int.Parse(value); break;
                        case "GATE_SM": gateSoftnessMultiplier = double.Parse(value); break;
                        case "GATE_SM_LO": gateSoftnessMultiplierLowOffset = double.Parse(value); break;
                        case "ADAPT": adaptiveDynamic = bool.Parse(value); break;
                        case "PT": pickTime = TimeSpan.Parse(value); break;
                        case "U_QBF": UseQualityBasedFiltering = bool.Parse(value); break;
                        case "Q_THR": QualityThreshold = double.Parse(value); break;
                        case "HPF": PP.hpCutoffHz = double.Parse(value); break;
                        case "BG_MUL": PP.baseGateMultiplier = double.Parse(value); break;
                        case "ATK_MS": PP.attackMs = double.Parse(value); break;
                        case "REL_MS": PP.releaseMs = double.Parse(value); break;
                        case "TRMS": PP.targetRms = double.Parse(value); break;
                        case "CLIP_DV": PP.clipDrive = double.Parse(value); break;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 파라미터를 상세 형식으로 출력합니다.
        /// 형식: 변수명 = 값  //  적용 함수 (설명)
        /// </summary>
        public string ToDetailedText()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== PickAudioFpParam 파라미터 ===");
            sb.AppendLine();
            
            // 오디오 처리 파라미터
            sb.AppendLine("[ 오디오 처리 파라미터 ]");
            sb.AppendLine($"mvCategoryId = {mvCategoryId}  //  카테고리 분류 (0:음악, 1:영화/오디오, 2:대사/효과음)");
            sb.AppendLine($"sampleRate = {sampleRate}  //  오디오 처리 (샘플레이트 Hz)");
            sb.AppendLine();
            
            // FingerprintConfig 파라미터
            sb.AppendLine("[ 핑거프린트 설정 (fptCfg) ]");
            sb.AppendLine($"fptCfg.FFTSize = {fptCfg.FFTSize}  //  Peak 검출 (FFT 크기)");
            sb.AppendLine($"fptCfg.HopSize = {fptCfg.HopSize}  //  Peak 검출 (Hop 크기)");
            sb.AppendLine($"fptCfg.mvHashOnly = {fptCfg.mvHashOnly}  //  해시 생성 (영화 해시 전용 여부)");
            sb.AppendLine($"fptCfg.mvRvIndex = {fptCfg.mvRvsIndex}  //  해시 생성 (영화 역인덱스 생성 여부)");
            sb.AppendLine($"fptCfg.PeakNeighborhoodSize = {fptCfg.PeakNeighborhoodSize}  //  Peak 검출 (이웃 크기)");
            sb.AppendLine($"fptCfg.MaxPeaksPerFrame = {fptCfg.MaxPeaksPerFrame}  //  Peak 검출 (프레임당 최대 피크 수)");
            sb.AppendLine($"fptCfg.PeakThresholdMultiplier = {fptCfg.PeakThresholdMultiplier}  //  Peak 검출 (평균+N*표준편차 임계값)");
            sb.AppendLine($"fptCfg.TimeQuantizationUnit = {fptCfg.TimeQuantizationUnit}  //  해시 생성 (시간 양자화 단위, 초)");
            sb.AppendLine($"fptCfg.FrequencyQuantizationUnit = {fptCfg.FrequencyQuantizationUnit}  //  해시 생성 (주파수 양자화 단위, Hz)");
            sb.AppendLine($"fptCfg.HashTimeWindow = {fptCfg.HashTimeWindow}  //  해시 생성 (시간 윈도우, 초)");
            sb.AppendLine();
            
            // 오프셋 집중도 기반 동적 확장/게이트 조정 파라미터
            sb.AppendLine("[ 동적 확장/게이트 조정 파라미터 ]");
            sb.AppendLine($"offsetConcntThreshold = {offsetConcntThreshold}  //  동적 조정 (오프셋 집중도 임계값, 미만시 보강)");
            sb.AppendLine($"maxWindowSizeMs = {maxWindowSizeMs}  //  동적 조정 (일반 최대 구간, ms)");
            sb.AppendLine($"maxWindowSizeMsLowOffset = {maxWindowSizeMsLowOffset}  //  동적 조정 (집중도 낮을 때 최대 구간, ms)");
            sb.AppendLine($"gateSoftnessMultiplier = {gateSoftnessMultiplier}  //  동적 조정 (게이트 부드러움, 클수록 부드러움)");
            sb.AppendLine($"gateSoftnessMultiplierLowOffset = {gateSoftnessMultiplierLowOffset}  //  동적 조정 (집중도 낮을 때 게이트 완화)");
            sb.AppendLine();
            
            // Audio Peak 추출 파라미터
            sb.AppendLine("[ Audio Peak 추출 파라미터 ]");
            sb.AppendLine($"adaptiveDynamic = {adaptiveDynamic}  //  Peak 추출 (동적 확장/게이트 조정 사용 여부)");
            sb.AppendLine($"pickTime = {pickTime}  //  Peak 추출 (추출 시작 시간)");
            sb.AppendLine();
            
            // 품질기반 필터링 파라미터
            sb.AppendLine("[ 품질기반 필터링 파라미터 ]");
            sb.AppendLine($"UseQualityBasedFiltering = {UseQualityBasedFiltering}  //  품질 필터링 (사용 여부)");
            sb.AppendLine($"QualityThreshold = {QualityThreshold}  //  품질 필터링 (최소 허용 품질 점수, 0-100)");
            sb.AppendLine();
            
            // 전처리 파라미터 (PP)
            sb.AppendLine("[ 전처리 파라미터 (PP) ]");
            sb.AppendLine($"PP.hpCutoffHz = {PP.hpCutoffHz}  //  전처리 (하이패스 필터 차단 주파수, Hz)");
            sb.AppendLine($"PP.baseGateMultiplier = {PP.baseGateMultiplier}  //  전처리 (기본 게이트 배율)");
            sb.AppendLine($"PP.attackMs = {PP.attackMs}  //  전처리 (어택 시간, ms)");
            sb.AppendLine($"PP.releaseMs = {PP.releaseMs}  //  전처리 (릴리즈 시간, ms)");
            sb.AppendLine($"PP.targetRms = {PP.targetRms}  //  전처리 (목표 RMS 레벨)");
            sb.AppendLine($"PP.clipDrive = {PP.clipDrive}  //  전처리 (클립 드라이브)");
            
            return sb.ToString();
        }
        public bool WriteToTextFile(string filePath, string Text)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return false;
            }
            string outputDir = Path.GetDirectoryName(filePath);
            string tmpFileName = Path.GetFileNameWithoutExtension(filePath);
            string outputFile = Path.Combine(outputDir, tmpFileName + ".par");
            if (!string.IsNullOrWhiteSpace(outputDir) && Directory.Exists(outputDir))
            {
                File.WriteAllText(outputFile, Text, Encoding.UTF8);
                return true;
            }
            return false;
        }


    }

    public class Bases
    {
        public static void WriteDiagToFile(string pickedFpPath, MatchDiagnostics diagResult, bool bMatched)
        {
            if (string.IsNullOrEmpty(pickedFpPath))
            {
                return;
            }
            string diagDir = Path.GetDirectoryName(pickedFpPath);
            string diagBaseName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(pickedFpPath));
            string diagFileName = $"{diagBaseName}_" + (bMatched ? "match_diag(OK).txt" : "match_diag(Fail).txt");
            string diagFilePath = Path.Combine(diagDir, diagFileName);

            if (string.IsNullOrWhiteSpace(diagDir) || Directory.Exists(diagDir))
            {
                if (diagResult == null)
                    File.WriteAllText(diagFilePath, "진단 결과가 없습니다.", Encoding.UTF8);
                else
                    File.WriteAllText(diagFilePath, diagResult.DiagnosisMessage, Encoding.UTF8);
            }
        }


        public static string GetShortHashString(byte[] hashBytes)
        {
            if (hashBytes == null || hashBytes.Length == 0)
                return string.Empty;
            StringBuilder sb = new StringBuilder();
            int displayLength = Math.Min(4, hashBytes.Length); // 처음 4바이트만 표시
            for (int i = 0; i < displayLength; i++)
            {
                sb.Append(hashBytes[i].ToString("X2")); // 16진수 대문자 형식
            }
            return sb.ToString();
        }

        public static string Diagnose_movieFptConfig(string fptFilePath, List<FptEntry> result, Dictionary<ulong, List<int>> movieRvsIndex)
        {
            StringBuilder sb = new StringBuilder();
     
            // ★ 원본 핑거프린트 FingerprintConfig 정보 로그 ★
            sb.Append($"\n★★★ [원본 FingerprintConfig] ★★★");
            sb.Append($"\n  원본 핑거프린트 파일: {Path.GetFileName(fptFilePath)}");
            sb.Append($"\n  총 엔트리 수: {result?.Count ?? 0}");
            sb.Append($"\n  총 해시 수: {result?.Sum(e => e.Hashes?.Count ?? 0) ?? 0}");
            sb.Append($"\n  역인덱스 해시 수: {movieRvsIndex?.Count ?? 0}");
            if (result != null && result.Count > 0)
            {
                var firstEntry = result.FirstOrDefault(e => e.Hashes != null && e.Hashes.Count > 0);
                if (firstEntry?.Hashes?.Count > 0)
                {
                    var sampleHash = firstEntry.Hashes[0];
                    sb.Append($"\n  샘플 해시: {sampleHash.Hash}");
                    sb.Append($"\n  샘플 메타: F1={sampleHash.Frequency1:F1}, F2={sampleHash.Frequency2:F1}, dt={sampleHash.TimeDelta:F4}, TimeMs={sampleHash.TimeMs}");
                }
            }
            sb.Append($"\n★★★ [원본 FingerprintConfig 끝] ★★★\n");

            return sb.ToString();
        }
        public static string Diagnose_liveFptConfig(TimeSpan currentStartTime, PickAudioFpParam pickParam, double[] samples)
        {
            StringBuilder sb = new StringBuilder();
            // ★ Live FingerprintConfig 파라미터 로그 ★
            sb.Append($"\n★★★ [Live FingerprintConfig] ★★★");
            sb.Append($"\n  SampleRate: {pickParam.sampleRate}Hz");
            sb.Append($"\n  FFTSize: {pickParam.fptCfg.FFTSize}");
            sb.Append($"\n  HopSize: {pickParam.fptCfg.HopSize}");
            sb.Append($"\n  MaxPeaksPerFrame: {pickParam.fptCfg.MaxPeaksPerFrame}");
            sb.Append($"\n  PeakNeighborhoodSize: {pickParam.fptCfg.PeakNeighborhoodSize}");
            sb.Append($"\n  PeakThresholdMultiplier: {pickParam.fptCfg.PeakThresholdMultiplier}");
            sb.Append($"\n  HashTimeWindow: {pickParam.fptCfg.HashTimeWindow}s");
            sb.Append($"\n  현재 시작 시간: {currentStartTime}");
            sb.Append($"\n  샘플 길이: {samples.Length} ({samples.Length / (double)pickParam.sampleRate:F2}초)");
            sb.Append($"\n★★★ [Live FingerprintConfig 끝] ★★★\n");

            return sb.ToString();
        }
    }
}
