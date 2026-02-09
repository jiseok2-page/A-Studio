using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AudioViewStudio.Analysis
{
    /// <summary>
    /// SFPFM (Sound Fingerprint Matching) 모듈
    /// 오디오 Sound 핑거프린트 방식으로 상영시점을 찾는 유사도 분석을 수행합니다.
    /// </summary>
    public static class SFPFM
    {
        private const int DefaultFFTSize = 4096;
        private const int DefaultHopSize = 2048; // 50% overlap
        private const double PeakThresholdRatio = 0.05; // 초기(0.1) 최대값의 10% 이상인 피크만 선택
        private const int PeakNeighborhoodSize = 5; // 피크 검출 시 주변 영역 크기
        private const int HashTimeWindow = 3; // 해시 생성 시 사용할 시간 윈도우 (초)
        private const int MaxPeaksPerWindow = 50; // 윈도우당 최대 peak 수 (성능 최적화: 모든 쌍 생성 대신 제한)
        
        // ★★★ 2026-02-07: 같은 프레임 Peak 제외 ★★★
        private const double MinTimeDeltaForHash = 0.02; // 최소 시간 차이 (20ms) - 같은 프레임 Peak 제외
        
        // Peak 밀도 기반 동적 Fan-out 최적화 상수
        private const double HighDensityThreshold = 15.0; // 초당 peak 수 (밀도 높음 기준)
        private const double LowDensityThreshold = 8.0; // 초당 peak 수 (밀도 낮음 기준)
        private const int LowDensity_FanOut = 10; // 60 밀도 낮음: 높은 Fan-out (해시 부족 방지)
        private const int MediumDensity_FanOut = 7; // 40 밀도 중간: ※ 40은 Shazam 권장값(5-10)의 4-8배입니다. 이로 인해 해시 식별력이 저하됩니다.
        private const int HighDensity_FanOut = 5; // 30 밀도 높음: 낮은 Fan-out (용량 절감)
        
        // SNR 기반 동적 추정 상수
        private const double MinSNRThresholdDb = -3.0; // 최소 SNR 임계값 (dB) - 이 이상일 때만 Peak 추출
        private const double SignalBandRatio = 0.1; // 상위 10% 주파수 대역 (신호 영역)
        private const double NoiseBandRatio = 0.2; // 하위 20% 주파수 대역 (노이즈 영역)

        // 품질 기반 Peak Detection 상수
        //private const double QualityThreshold = 60.0; // 최소 허용 품질 점수 (0-100)
        //private const bool UseQualityBasedFiltering = true; // 품질 기반 필터링 사용 여부
        private const bool UseGaussianSmoothingThreshold = true; // Gaussian Smoothing 기반 Threshold 사용 여부
        
        // 동적 윈도우 확장 상수
        private const int MinWindowSizeMs = 200; // 최소 구간 (ms)
        private const int MaxWindowSizeMs = 700; // 최대 구간 (ms)
        private const int WindowExpansionStepMs = 100; // 구간 확장 단계 (ms)
        private const int MinPeaksRequired = 5; // 최소 Peak 개수
        private const bool UseDynamicWindowExpansion = true; // 동적 윈도우 확장 사용 여부
        
        // Combinatorial Hashing 설정
        private const bool UseCombinatorialHashing = true; // Combinatorial Hashing 사용 여부
        private const int CombinatorialHashingStep = 3; // Triplet 생성 시 step 간격 (메모리 절감)
        
        // ★★★ 과다 출현 해시 필터링 임계값 (통합 상수) ★★★
        // 이 값보다 많이 출현하는 해시는 노이즈로 간주하여 필터링됨
        // ★★★ 2026-02-08: 20 → 100으로 상향 (유효 해시 손실 방지) ★★★
        // 이전: 20회 → 유효 해시 필터링 (800~810초 구간 매칭 실패 원인)
        // 권장 범위: 50-100 (2시간 영화 기준)
        public const int DefaultMaxHashOccurrences = 100;
        
        // Phase 1: 청크 단위 스트리밍 처리 상수
        // 구조 개선: 적응형 청크 크기 사용 (Peak 밀도 기반)
        private const double BaseChunkSizeSeconds = 30.0; // 기본 청크 크기: 30초
        private const double ChunkOverlapSeconds = 3.0; // 청크 오버랩: 3초 (HashTimeWindow와 동일, 경계 해시 누락 방지)
        private const int TargetPeaksPerChunk = 50000; // 청크당 목표 peak 수 (적응형 크기 계산용)
        
        // 구조 개선: 배치 병합을 위한 상수 (lock 경합 감소)
        // 메모리 부족 방지를 위해 배치 크기를 더 줄임
        private const int ChunkBatchSize = 2; // 배치 크기: 2개 청크를 모아서 한 번에 병합 (3개에서 감소)
        
        // 구조 개선: 스트리밍 방식 - 완료된 타임스탬프 즉시 해제를 위한 상수
        // ExecutionContext.cs OutOfMemoryException 방지: 윈도우를 더 크게 하여 더 자주 해제
        private const int TimestampCompletionWindow = 10; // 완료된 타임스탬프 판단 윈도우 (초) (5초에서 증가)
        
        // 스트리밍 처리 상수: Feature 추출과 핑거프린트 생성을 동시에 수행
        private const double StreamingWindowSeconds = 10.0; // 스트리밍 윈도우 크기: 10초 (메모리 사용량 제어)
        private const double StreamingOverlapSeconds = 3.0; // 스트리밍 오버랩: 3초 (HashTimeWindow와 동일)
        private const int MaxPeaksInMemory = 50000; // 메모리에 유지할 최대 피크 수 (약 10초 분량)
        
        // 메모리 기반 스레드 수 계산 상수
        // Thread.cs의 OutOfMemoryException 방지를 위해 매우 보수적으로 설정
        private const long ThreadStackSizeBytes = 2 * 1024 * 1024; // 스레드 스택 크기: 2MB (보수적 추정, 실제로는 더 클 수 있음)
        private const long ThreadLocalDictionaryOverheadBytes = 15000 * 40; // ThreadLocal Dictionary 초기 오버헤드: 약 600KB
        private const long MaxMemoryPerThreadBytes = ThreadStackSizeBytes + ThreadLocalDictionaryOverheadBytes; // 스레드당 최대 메모리: 약 2.6MB
        private const long StableMemoryBudgetBytes = 30 * 1024 * 1024; // 안정적인 메모리 예산: 30MB (보수적으로 감소)
        private const int MaxThreadsHardLimit = 1; // 단일 Thread(1), 하드 리미트(n): 최대 n개 

        /// <summary>
        /// Original-FP: 영화 전체 상영시간에 대한 핑거프린트를 추출하여 파일로 저장합니다.
        /// </summary>
        /// <param name="audioFilePath">영화 오디오 파일 경로 (WAV 형식)</param>
        /// <param name="outputFilePath">핑거프린트 출력 파일 경로</param>
        /// <param name="progress">진행 상황 보고용 Progress 객체</param>
        /// <param name="cancellationToken">취소 토큰</param>
        /// <param name="pauseToken">일시 정지 토큰</param>
        /// <param name="fftSize">FFT 크기 (기본값: 2048)</param>
        /// <param name="hopSize">Hop 크기 (기본값: 1024)</param>
        /// <returns>추출 결과</returns>
        public static async Task<OriginalFptResult> ExtractOriginalFPAsync(
            PickAudioFpParam param,
            string audioFilePath,
            string outputFilePath,
            IProgress<OriginalFPProgress> progress,
            CancellationToken cancelToken,
            object pauseToken = null,
            //bool hashOnly = false,
            Action<string> statusMsgCbk = null)
        {
            if (string.IsNullOrWhiteSpace(audioFilePath))
            {
                throw new ArgumentException("오디오 파일 경로는 비어 있을 수 없습니다.", nameof(audioFilePath));
            }

            if (!File.Exists(audioFilePath))
            {
                throw new FileNotFoundException("오디오 파일을 찾을 수 없습니다.", audioFilePath);
            }

            if (string.IsNullOrWhiteSpace(outputFilePath))
            {
                throw new ArgumentException("출력 파일 경로는 비어 있을 수 없습니다.", nameof(outputFilePath));
            }

            int actualFFTSize = param.fptCfg.FFTSize; 
            int actualHopSize = param.fptCfg.HopSize; 
            
            // FFT 크기는 2의 거듭제곱이어야 함 (UI에서 이미 2의 거듭제곱만 선택 가능하지만, 안전을 위해 검증)
            int power = 1;
            while (power < actualFFTSize) power <<= 1;
            actualFFTSize = power;
            
            power = 1;
            while (power < actualHopSize) power <<= 1;
            actualHopSize = power;
            
            // Hop 크기는 FFT 크기보다 작아야 함
            if (actualHopSize >= actualFFTSize)
            {
                actualHopSize = actualFFTSize / 2;
            }
            
            return await Task.Run(() =>
                ExtractOriginalFPInternal(param, audioFilePath, outputFilePath, progress, cancelToken, pauseToken, statusMsgCbk),
                cancelToken);
        }

        private static OriginalFptResult ExtractOriginalFPInternal(
            PickAudioFpParam param,
            string audioFilePath,
            string outputFilePath,
            IProgress<OriginalFPProgress> progress,
            CancellationToken cancellationToken,
            object pauseToken,
            //bool hashOnly = false,
            Action<string> statusMsgCbk = null)
        {
            var fingerprints = new List<FptEntry>();
            int audioSampleRate = 0; // 오디오 파일의 실제 샘플레이트 (Live 매칭용)
            
            // 구조 개선: 중간 파일 경로 생성
            string peaksFilePath = GetIntermediateFilePath(outputFilePath, "peaks");
            string fingerprintsFilePath = GetIntermediateFilePath(outputFilePath, "fingerprints");
            
            // 중간 파일 확인 및 로드
            List<Peak> peaksList = null;
            bool peaksLoaded = false;
            bool fingerprintsLoaded = false;
            
            // 1. Fingerprint 중간 파일 확인 (최종 결과가 이미 있으면 완전히 건너뛰기)
            if (File.Exists(outputFilePath))
            {
                try
                {
                    if (statusMsgCbk != null)
                    {
                        statusMsgCbk($"기존 핑거프린트 파일 로드 중... ({Path.GetFileName(outputFilePath)})");
                    }
                    fingerprints = LoadFingerprintsFromFile(outputFilePath);
                    if (fingerprints != null && fingerprints.Count > 0)
                    {
                        if (statusMsgCbk != null)
                        {
                            statusMsgCbk($"기존 핑거프린트 파일 발견 ({fingerprints.Count}개). 재사용합니다.");
                        }
                        return new OriginalFptResult
                        {
                            TotalFingerprints = fingerprints.Count,
                            OutputFilePath = outputFilePath,
                            WasCanceled = false
                        };
                    }
                }
                catch
                {
                    // 로드 실패 시 계속 진행
                }
            }
            
            // 2. Fingerprint 중간 파일 확인
            if (File.Exists(fingerprintsFilePath))
            {
                try
                {
                    if (statusMsgCbk != null)
                    {
                        statusMsgCbk($"기존 핑거프린트 중간 파일 로드 중... ({Path.GetFileName(fingerprintsFilePath)})");
                    }
                    fingerprints = LoadFingerprintsFromFile(fingerprintsFilePath);
                    if (fingerprints != null && fingerprints.Count > 0)
                    {
                        fingerprintsLoaded = true;
                        if (statusMsgCbk != null)
                        {
                            statusMsgCbk($"기존 핑거프린트 중간 파일 발견 ({fingerprints.Count}개). 재사용합니다.");
                        }
                    }
                }
                catch
                {
                    // 로드 실패 시 계속 진행
                }
            }
            
            // 3. Peak 중간 파일 확인
            if (File.Exists(peaksFilePath))
            {
                try
                {
                    if (statusMsgCbk != null)
                    {
                        statusMsgCbk($"기존 Peak 파일 로드 중... ({Path.GetFileName(peaksFilePath)})");
                    }
                    peaksList = LoadPeaksFromFile(peaksFilePath, progress, statusMsgCbk, cancellationToken);
                    if (peaksList != null && peaksList.Count > 0)
                    {
                        peaksLoaded = true;
                        if (statusMsgCbk != null)
                        {
                            statusMsgCbk($"기존 Peak 파일 로드 완료 ({peaksList.Count}개 Peak)");
                        }
                    }
                    else
                    {
                        // 취소 상태 확인: null 반환 시 취소된 것일 수 있음
                        if (cancellationToken.IsCancellationRequested)
                        {
                            peaksList?.Clear();
                            return new OriginalFptResult
                            {
                                TotalFingerprints = 0,
                                OutputFilePath = outputFilePath,
                                WasCanceled = true
                            };
                        }
                        
                        if (statusMsgCbk != null)
                        {
                            statusMsgCbk($"Peak 파일 로드 실패: 파일이 비어있거나 손상되었습니다.");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 취소 요청 시 즉시 중단
                    peaksList?.Clear();
                    return new OriginalFptResult
                    {
                        TotalFingerprints = 0,
                        OutputFilePath = outputFilePath,
                        WasCanceled = true
                    };
                }
                catch (Exception ex)
                {
                    // 취소 상태 확인
                    if (cancellationToken.IsCancellationRequested)
                    {
                        peaksList?.Clear();
                        return new OriginalFptResult
                        {
                            TotalFingerprints = 0,
                            OutputFilePath = outputFilePath,
                            WasCanceled = true
                        };
                    }
                    
                    // 로드 실패 시 상세 정보 로깅
                    if (statusMsgCbk != null)
                    {
                        try
                        {
                            statusMsgCbk($"Peak 파일 로드 실패: {ex.GetType().Name} - {ex.Message}");
                        }
                        catch { }
                    }
                    System.Diagnostics.Debug.WriteLine($"LoadPeaksFromFile 예외: {ex}");
                    // 로드 실패 시 계속 진행
                }
            }
            
            var context = ParseWaveHeader(audioFilePath);
            int sampleRate = context.SampleRate;
            audioSampleRate = sampleRate; // Live 매칭용 샘플레이트 저장
            int channels = context.Channels;
            long totalSamples = context.TotalSamples;
            int fftSize = param.fptCfg.FFTSize;
            int hopSize = param.fptCfg.HopSize;

            // 청크 버퍼링 방식: 262144 샘플씩 읽어서 버퍼링하여 I/O 횟수 감소
            // 최적화: 2MB 버퍼로 최대 I/O 효율성 확보 (약 128개 프레임 포함)
            const int chunkSize = 262144; // 메모리 사용량: 262144 샘플 * 8바이트 = 약 2MB
            int bytesPerSample = context.BitsPerSample / 8;
            
            // 모노 샘플 기준으로 총 프레임 수 계산
            long totalMonoSamples = channels == 2 ? totalSamples / 2 : totalSamples;
            int totalFrames = (int)Math.Ceiling((totalMonoSamples - fftSize) / (double)hopSize) + 1;

            // 취소 상태 확인: Peak 추출 전 취소 체크
            if (cancellationToken.IsCancellationRequested)
            {
                peaksList?.Clear();
                fingerprints?.Clear();
                return new OriginalFptResult
                {
                    TotalFingerprints = 0,
                    OutputFilePath = outputFilePath,
                    WasCanceled = true
                };
            }

            // Peak 추출 단계 (중간 파일이 없을 때만 실행)
            if (!peaksLoaded)
            {
                // 최적화: 병렬 처리를 위한 Thread-safe 피크 컬렉션
                // 피크 리스트 크기 추정: 초당 약 10-50개 피크 가정 (영화 파일 길이 기준)
                int estimatedPeaks = (int)(totalMonoSamples / (double)sampleRate * 30); // 초당 30개 피크 가정
                var peaks = new ConcurrentBag<Peak>();
                
                // 최적화: 윈도우 함수 사전 계산 및 재사용
                double[] hammingWindow = new double[fftSize];
                for (int i = 0; i < fftSize; i++)
                {
                    hammingWindow[i] = fftSize > 1 ? 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (fftSize - 1)) : 1.0;
                }
                
                int spectrumLength = fftSize / 2;
                double[] frequencies = new double[spectrumLength];
                
                // 주파수 배열은 한 번만 계산
                for (int i = 0; i < spectrumLength; i++)
                {
                    frequencies[i] = i * sampleRate / (double)fftSize;
                }

                using (var stream = File.Open(audioFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false))
                {
                // 버퍼 관리 변수
                double[] buffer = null; // 현재 버퍼
                long bufferStartMonoIndex = -1; // 버퍼의 시작 모노 샘플 인덱스
                int bufferLength = 0; // 버퍼에 실제로 들어있는 샘플 수

                // 병렬 처리: 버퍼 단위로 프레임들을 배치 처리
                int processedFrames = 0;
                int lastReportedFrames = 0;
                object progressLock = new object();

                int frameIndex = 0;
                int lastProcessedFrameIndex = -1; // 무한 루프 방지용
                
                while (frameIndex < totalFrames)
                {
                    // 무한 루프 방지: 같은 프레임을 반복 처리하는 경우 종료
                    if (frameIndex <= lastProcessedFrameIndex)
                    {
                        break;
                    }
                    lastProcessedFrameIndex = frameIndex;
                    
                    // 취소 체크 (정식 취소 요청 처리: 예외 없이 루프 종료)
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // 취소 요청 시 버퍼 정리 후 루프 종료
                        buffer = null;
                        break;
                    }
                    
                    if (pauseToken != null && pauseToken is AudioFeatures.PauseTokenSource pauseTokenSource)
                    {
                        pauseTokenSource.WaitIfPaused(cancellationToken);
                    }

                    // 모노 샘플 기준 시작 위치
                    long monoStartIndex = frameIndex * (long)hopSize;
                    
                    // 파일 끝 체크
                    if (monoStartIndex >= totalMonoSamples)
                    {
                        break;
                    }

                    // 새 버퍼 읽기
                    long chunkStartMonoIndex = (monoStartIndex / chunkSize) * chunkSize;
                    
                    if (chunkStartMonoIndex >= totalMonoSamples)
                    {
                        break;
                    }
                    
                    int samplesToRead = chunkSize;
                    
                    if (chunkStartMonoIndex + samplesToRead > totalMonoSamples)
                    {
                        samplesToRead = (int)(totalMonoSamples - chunkStartMonoIndex);
                    }
                    
                    if (samplesToRead <= 0)
                    {
                        break;
                    }
                    
                    long fileStartPosition = context.DataStartPosition + (chunkStartMonoIndex * channels * bytesPerSample);
                    
                    if (fileStartPosition >= context.DataStartPosition + context.DataLength)
                    {
                        break;
                    }

                    // 버퍼 읽기 (순차 처리)
                    reader.BaseStream.Position = fileStartPosition;
                    buffer = ReadChunkSamples(reader, context.AudioFormat, context.BitsPerSample, channels, samplesToRead, cancellationToken);
                    
                    // 취소 상태 확인 (ReadChunkSamples가 null을 반환하면 취소 요청)
                    if (buffer == null || cancellationToken.IsCancellationRequested)
                    {
                        // 취소 요청 시 버퍼 정리 후 루프 종료
                        buffer = null;
                        break;
                    }
                    
                    bufferStartMonoIndex = chunkStartMonoIndex;
                    bufferLength = buffer.Length;
                    
                    if (bufferLength == 0)
                    {
                        break; // 버퍼 읽기 실패
                    }

                    // 버퍼 내의 모든 프레임 인덱스 계산
                    int bufferStartFrameIndex = frameIndex;
                    int bufferEndFrameIndex = frameIndex;
                    
                    // 버퍼 내에 포함될 수 있는 프레임 범위 계산
                    long bufferEndMonoIndex = bufferStartMonoIndex + bufferLength;
                    
                    while (bufferEndFrameIndex < totalFrames)
                    {
                        long frameStart = bufferEndFrameIndex * (long)hopSize;
                        long frameEnd = frameStart + fftSize;
                        
                        // ★★★ 2026-02-03: 프레임 끝 위치도 확인 ★★★
                        // 프레임의 끝 위치가 버퍼 범위를 벗어나면 다음 버퍼를 읽어야 함
                        // (제로 패딩 방지)
                        if (frameEnd > bufferEndMonoIndex)
                        {
                            break; // 버퍼 범위를 벗어남 - 다음 버퍼에서 처리
                        }
                        
                        // 파일 끝 체크: 프레임 끝 위치가 파일 끝을 넘어가면 처리 불가
                        if (frameEnd > totalMonoSamples)
                        {
                            break;
                        }
                        
                        bufferEndFrameIndex++;
                    }
                    
                    int framesInBuffer = bufferEndFrameIndex - bufferStartFrameIndex;
                    
                    if (framesInBuffer > 0)
                    {
                        // 병렬 처리: 버퍼 내의 모든 프레임을 병렬로 처리
                        // 메모리 기반 스레드 수 계산: 안정적인 메모리 사용량을 기준으로 스레드 수 결정
                        long availableMemoryFrame = GC.GetTotalMemory(false);
                        long usableMemoryFrame = Math.Min(availableMemoryFrame, StableMemoryBudgetBytes);
                        // 프레임 처리는 ThreadLocal을 사용하지 않으므로 스레드 스택만 고려
                        int maxFrameThreadsByMemory = (int)((usableMemoryFrame * 0.5) / ThreadStackSizeBytes);

                        if(MaxThreadsHardLimit == 1) {
                            maxFrameThreadsByMemory = 1; // 하드 리미트 적용 (스레드 1개 고정)
                        }
                        int maxFrameThreads = Math.Min(Environment.ProcessorCount, Math.Max(1, Math.Min(maxFrameThreadsByMemory, 4)));
                        
                        // Thread.cs GetCurrentThreadNative() 크래시 방지: Parallel.For를 try-catch로 감싸기
                        try
                        {
                            // 병렬 처리 시작 전 메모리 정리 (Thread.CurrentThread 호출 시 메모리 부족 방지)
                            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                            GC.WaitForPendingFinalizers();
                            
                            Parallel.For(bufferStartFrameIndex, bufferEndFrameIndex, new ParallelOptions
                            {
                                CancellationToken = cancellationToken,
                                MaxDegreeOfParallelism = maxFrameThreads
                            }, (idx) =>
                            {
                                ProcessFrame(param, idx, buffer, bufferStartMonoIndex, bufferLength,
                                    hammingWindow, frequencies, sampleRate, totalMonoSamples, peaks);
                                
                                // 진행 상황 업데이트 (Thread-safe)
                                int current = Interlocked.Increment(ref processedFrames);
                                
                                // 진행 상황 보고: 500프레임마다 또는 마지막 10프레임 근처에서 자주 보고
                                bool shouldReport = false;
                                if (current - lastReportedFrames >= 500)
                                {
                                    shouldReport = true;
                                }
                                else if (current >= totalFrames - 10) // 마지막 10프레임은 매번 보고
                                {
                                    shouldReport = true;
                                }
                                
                                if (progress != null && shouldReport)
                                {
                                    lock (progressLock)
                                    {
                                        if (current - lastReportedFrames >= 500 || current >= totalFrames - 10)
                                    {
                                        double timeInSeconds = (idx * (long)hopSize) / (double)sampleRate;
                                        double progressPercent = (double)current / totalFrames * 100;
                                        progress.Report(new OriginalFPProgress
                                        {
                                            ProcessedFrames = current,
                                            TotalFrames = totalFrames,
                                            ProgressPercent = progressPercent,
                                            CurrentTime = TimeSpan.FromSeconds(timeInSeconds),
                                            CurrentAction = $"Peak 추출 중... ({current}/{totalFrames}, {progressPercent:0.0}%)"
                                        });
                                        lastReportedFrames = current;
                                    }
                                }
                            }
                        });
                        }
                        catch (OutOfMemoryException)
                        {
                            // Thread.cs GetCurrentThreadNative() 크래시 방지: 스레드 생성 실패 시 순차 처리로 폴백
                            if (statusMsgCbk != null)
                            {
                                try
                                {
                                    statusMsgCbk("프레임 병렬 처리 실패로 순차 처리로 전환합니다...");
                                }
                                catch { }
                            }
                            
                            // 순차 처리로 폴백
                            for (int idx = bufferStartFrameIndex; idx < bufferEndFrameIndex; idx++)
                            {
                                if (cancellationToken.IsCancellationRequested)
                                    break;
                                    
                                ProcessFrame(param, idx, buffer, bufferStartMonoIndex, bufferLength,
                                    hammingWindow, frequencies, sampleRate, totalMonoSamples, peaks);
                                
                                int current = Interlocked.Increment(ref processedFrames);
                                
                                if (progress != null && current % 500 == 0)
                                {
                                    lock (progressLock)
                                    {
                                        double timeInSeconds = (idx * (long)hopSize) / (double)sampleRate;
                                        double progressPercent = (double)current / totalFrames * 100;
                                        progress.Report(new OriginalFPProgress
                                        {
                                            ProcessedFrames = current,
                                            TotalFrames = totalFrames,
                                            ProgressPercent = progressPercent,
                                            CurrentTime = TimeSpan.FromSeconds(timeInSeconds),
                                            CurrentAction = $"Peak 추출 중... ({current}/{totalFrames}, {progressPercent:0.0}%)"
                                        });
                                        lastReportedFrames = current;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // Thread.cs GetCurrentThreadNative() 크래시 등 기타 예외 처리
                            if (statusMsgCbk != null)
                            {
                                try
                                {
                                    statusMsgCbk($"프레임 병렬 처리 오류: {ex.GetType().Name}. 순차 처리로 전환합니다...");
                                }
                                catch { }
                            }
                            
                            // 순차 처리로 폴백
                            for (int idx = bufferStartFrameIndex; idx < bufferEndFrameIndex; idx++)
                            {
                                if (cancellationToken.IsCancellationRequested)
                                    break;
                                    
                                ProcessFrame(param, idx, buffer, bufferStartMonoIndex, bufferLength,
                                    hammingWindow, frequencies, sampleRate, totalMonoSamples, peaks);
                                
                                int current = Interlocked.Increment(ref processedFrames);
                                
                                if (progress != null && current % 500 == 0)
                                {
                                    lock (progressLock)
                                    {
                                        double timeInSeconds = (idx * (long)hopSize) / (double)sampleRate;
                                        double progressPercent = (double)current / totalFrames * 100;
                                        progress.Report(new OriginalFPProgress
                                        {
                                            ProcessedFrames = current,
                                            TotalFrames = totalFrames,
                                            ProgressPercent = progressPercent,
                                            CurrentTime = TimeSpan.FromSeconds(timeInSeconds),
                                            CurrentAction = $"Peak 추출 중... ({current}/{totalFrames}, {progressPercent:0.0}%)"
                                        });
                                        lastReportedFrames = current;
                                    }
                                }
                            }
                        }
                        
                        frameIndex = bufferEndFrameIndex;
                    }
                    else
                    {
                        // framesInBuffer가 0인 경우 - 다음 프레임으로 진행
                        frameIndex++;
                    }
                    
                    // 종료 체크: 모든 프레임을 처리했는지 확인
                    if (frameIndex >= totalFrames)
                    {
                        break;
                    }
                }
                
                // 마지막 진행 상황 보고 (프레임 처리 100% 완료)
                if (progress != null)
                {
                    progress.Report(new OriginalFPProgress
                    {
                        ProcessedFrames = totalFrames,
                        TotalFrames = totalFrames,
                        ProgressPercent = 100.0,
                        CurrentTime = TimeSpan.FromSeconds(totalMonoSamples / (double)sampleRate),
                        CurrentAction = "Peak 추출 완료"
                    });
                }
                
                // Peak 변환 (ConcurrentBag을 List로 변환)
                // 메모리 최적화: ToList() 대신 직접 변환
                if (statusMsgCbk != null)
                {
                    statusMsgCbk($"특징 추출 완료. Peak 변환 중... (총 {peaks.Count}개)");
                }
                
                peaksList = new List<Peak>(peaks.Count);
                int peakConversionCount = 0;
                foreach (var peak in peaks)
                {
                    peaksList.Add(peak);
                    peakConversionCount++;
                    
                    // 10만 개마다 진행 상황 표시
                    if (peakConversionCount % 100000 == 0 && statusMsgCbk != null)
                    {
                        int percent = (int)((double)peakConversionCount / peaks.Count * 100);
                        statusMsgCbk($"Peak 변환 중... ({peakConversionCount}/{peaks.Count}, {percent}%)");
                    }
                }
                
                // 취소 상태 확인: 취소된 경우 Peak 저장 및 후속 작업 모두 건너뛰기
                if (cancellationToken.IsCancellationRequested)
                {
                    // 취소 시 메모리 정리 후 즉시 반환
                    peaksList?.Clear();
                    // peaks는 ConcurrentBag이므로 Clear() 메서드가 없음 - null로 설정하여 GC가 처리하도록 함
                    return new OriginalFptResult
                    {
                        TotalFingerprints = 0,
                        OutputFilePath = outputFilePath,
                        WasCanceled = true
                    };
                }
                
                // 구조 개선: Peak 중간 파일 저장
                try
                {
                    // 파라미터 유효성 검사
                    if (peaksList == null)
                    {
                        if (statusMsgCbk != null)
                        {
                            statusMsgCbk("Peak 중간 파일 저장 실패: peaksList가 null입니다.");
                        }
                    }
                    else if (peaksList.Count == 0)
                    {
                        if (statusMsgCbk != null)
                        {
                            statusMsgCbk("Peak 중간 파일 저장 실패: peaksList가 비어있습니다.");
                        }
                    }
                    else if (string.IsNullOrEmpty(peaksFilePath))
                    {
                        if (statusMsgCbk != null)
                        {
                            statusMsgCbk("Peak 중간 파일 저장 실패: peaksFilePath가 null이거나 비어있습니다.");
                        }
                    }
                    else if (context == null)
                    {
                        if (statusMsgCbk != null)
                        {
                            statusMsgCbk("Peak 중간 파일 저장 실패: context가 null입니다.");
                        }
                    }
                    else
                    {
                        // 취소 상태 재확인: 저장 전에 다시 체크
                        if (cancellationToken.IsCancellationRequested)
                        {
                            peaksList?.Clear();
                            return new OriginalFptResult
                            {
                                TotalFingerprints = 0,
                                OutputFilePath = outputFilePath,
                                WasCanceled = true
                            };
                        }
                        
                        // 실제 저장 실행
                        SavePeaksToFile(peaksList, peaksFilePath, context);
                        if (statusMsgCbk != null)
                        {
                            statusMsgCbk($"Peak 중간 파일 저장 완료: {Path.GetFileName(peaksFilePath)} ({peaksList.Count}개 Peak)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (statusMsgCbk != null)
                    {
                        try
                        {
                            statusMsgCbk($"Peak 중간 파일 저장 실패: {ex.GetType().Name} - {ex.Message}");
                        }
                        catch { }
                    }
                    // 예외를 다시 던지지 않고 계속 진행
                    System.Diagnostics.Debug.WriteLine($"SavePeaksToFile 예외: {ex}");
                }
                
                // 취소 상태 확인: Peak 저장 후 취소 체크
                if (cancellationToken.IsCancellationRequested)
                {
                    peaksList?.Clear();
                    return new OriginalFptResult
                    {
                        TotalFingerprints = 0,
                        OutputFilePath = outputFilePath,
                        WasCanceled = true
                    };
                }
                
                if (statusMsgCbk != null)
                {
                    statusMsgCbk($"Peak 변환 완료 ({peaks.Count}개). 핑거프린트 생성 시작...");
                }
                }
            }
            
            // 취소 상태 확인: Peak 로드 후 취소 체크
            if (cancellationToken.IsCancellationRequested)
            {
                peaksList?.Clear();
                return new OriginalFptResult
                {
                    TotalFingerprints = 0,
                    OutputFilePath = outputFilePath,
                    WasCanceled = true
                };
            }
            
            if (peaksLoaded && statusMsgCbk != null)
            {
                statusMsgCbk($"기존 Peak 파일 로드 완료 ({peaksList?.Count ?? 0}개). 핑거프린트 생성 시작...");
            }
            
            // peaksList가 null이면 오류
            if (peaksList == null || peaksList.Count == 0)
            {
                if (statusMsgCbk != null)
                {
                    statusMsgCbk("Peak 데이터가 없습니다.");
                }
                return new OriginalFptResult
                {
                    TotalFingerprints = 0,
                    OutputFilePath = outputFilePath,
                    WasCanceled = cancellationToken.IsCancellationRequested
                };
            }
            
            // 취소 상태 확인: Fingerprint 생성 전 취소 체크
            if (cancellationToken.IsCancellationRequested)
            {
                peaksList?.Clear();
                fingerprints?.Clear();
                return new OriginalFptResult
                {
                    TotalFingerprints = 0,
                    OutputFilePath = outputFilePath,
                    WasCanceled = true
                };
            }

            // 핑거프린트 생성 단계 (중간 파일이 없을 때만 실행)
            bool hashOnly = param.fptCfg.mvHashOnly;
            bool reverseIndex = param.fptCfg.mvRvsIndex;

            if (!fingerprintsLoaded)
            {
                // 핑거프린트 생성 단계 메시지 전달
                string initialAction = "핑거프린트 생성 시작... (0.0%)";
                if (statusMsgCbk != null)
                {
                    statusMsgCbk(initialAction);
                }
                
                if (progress != null)
                {
                    try
                    {
                        progress.Report(new OriginalFPProgress
                        {
                            ProcessedFrames = 0,
                            TotalFrames = peaksList?.Count ?? 0,
                            ProgressPercent = 0.0,
                            CurrentTime = TimeSpan.Zero,
                            CurrentAction = initialAction
                        });
                    }
                    catch { }
                }
                
                // 취소 상태 확인: Fingerprint 생성 시작 전 취소 체크
                if (cancellationToken.IsCancellationRequested)
                {
                    peaksList?.Clear();
                    fingerprints?.Clear();
                    return new OriginalFptResult
                    {
                        TotalFingerprints = 0,
                        OutputFilePath = outputFilePath,
                        WasCanceled = true
                    };
                }
                
                // 핑거프린트 생성 (시간이 오래 걸릴 수 있음)
                // 구조 개선: 메모리 정리 후 핑거프린트 생성 (메모리 부족 방지)
                // MainForm.Designer.cs:18 크래시 방지: WaitForPendingFinalizers 제거 (블로킹 방지)
                try
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                    // WaitForPendingFinalizers 제거: 블로킹 방지
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                    // WaitForPendingFinalizers 제거: 블로킹 방지
                    
                    // ★★★ 원본 생성 시 Dynamic Fan-out 통계 리셋 ★★★
                    ResetFanOutDiagnostics();
                    
                    fingerprints = GenerateFingerprints(peaksList, sampleRate, progress, statusMsgCbk);
                    
                    // ★★★ 원본 생성 시 Dynamic Fan-out 통계 출력 ★★★
                    System.Diagnostics.Debug.WriteLine($"\n★★★ [원본 Peak 정보] ★★★");
                    System.Diagnostics.Debug.WriteLine($"  총 Peak 수: {peaksList.Count}개");
                    PrintFanOutDiagnostics();
                }
                catch (OutOfMemoryException ex)
                {
                    // 메모리 부족 예외 처리
                    if (statusMsgCbk != null)
                    {
                        try
                        {
                            statusMsgCbk($"메모리 부족으로 핑거프린트 생성 실패: {ex.Message}");
                        }
                        catch { }
                    }
                    
                    // 메모리 정리 시도 (블로킹 방지: WaitForPendingFinalizers 제거)
                    try
                    {
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                        // WaitForPendingFinalizers 제거: 블로킹 방지
                    }
                    catch { }
                    
                    // 빈 리스트 반환 (실패 처리)
                    fingerprints = new List<FptEntry>();
                }
                catch (Exception ex)
                {
                    // 기타 예외 처리
                    if (statusMsgCbk != null)
                    {
                        try
                        {
                            statusMsgCbk($"핑거프린트 생성 중 오류 발생: {ex.GetType().Name} - {ex.Message}");
                        }
                        catch { }
                    }
                    
                    // 빈 리스트 반환 (실패 처리)
                    fingerprints = new List<FptEntry>();
                }
                
                // 취소 상태 확인: Fingerprint 생성 후 취소 체크
                if (cancellationToken.IsCancellationRequested)
                {
                    peaksList?.Clear();
                    fingerprints?.Clear();
                    return new OriginalFptResult
                    {
                        TotalFingerprints = 0,
                        OutputFilePath = outputFilePath,
                        WasCanceled = true
                    };
                }

                // 구조 개선: Fingerprint 중간 파일 저장
                // 최적화: 최종 파일이 없을 때만 중간 파일 저장 (중복 저장 방지)
                if (!File.Exists(outputFilePath))
                {
                    // 취소 상태 확인: 저장 전 취소 체크
                    if (cancellationToken.IsCancellationRequested)
                    {
                        peaksList?.Clear();
                        fingerprints?.Clear();
                        return new OriginalFptResult
                        {
                            TotalFingerprints = 0,
                            OutputFilePath = outputFilePath,
                            WasCanceled = true
                        };
                    }
                    
                    try
                    {
                        Save_movieFptsToFile(fingerprints, fingerprintsFilePath, context, useQuantization: true, hashOnly: hashOnly, statusMsgCbk: statusMsgCbk);
                        if (statusMsgCbk != null)
                        {
                            statusMsgCbk($"핑거프린트 중간 파일 저장 완료: {Path.GetFileName(fingerprintsFilePath)}");
                        }
                    }
                    catch (Exception ex)
                    {
                        if (statusMsgCbk != null)
                        {
                            try
                            {
                                statusMsgCbk($"핑거프린트 중간 파일 저장 실패: {ex.GetType().Name} - {ex.Message}");
                            }
                            catch { }
                        }
                        System.Diagnostics.Debug.WriteLine($"SaveFingerprintsToFile 예외: {ex}");
                        throw; // 예외를 다시 던져서 호출자가 처리할 수 있도록
                    }
                }
            }

            // 취소 상태 확인: 파일 저장 전 취소 체크
            if (cancellationToken.IsCancellationRequested)
            {
                peaksList?.Clear();
                fingerprints?.Clear();
                return new OriginalFptResult
                {
                    TotalFingerprints = 0,
                    OutputFilePath = outputFilePath,
                    WasCanceled = true
                };
            }

            if (statusMsgCbk != null)
            {
                statusMsgCbk($"핑거프린트 생성 완료 ({fingerprints.Count}개). 파일 저장 시작...");
            }
            
            // ★★★ 원본 생성 직후 해시 개수 분석 (Live 비교용) ★★★
            {
                int totalHashCount = 0;
                double maxTimestamp = 0;
                foreach (var entry in fingerprints)
                {
                    if (entry.Hashes != null)
                    {
                        totalHashCount += entry.Hashes.Count;
                    }
                    if (entry.Timestamp > maxTimestamp)
                    {
                        maxTimestamp = entry.Timestamp;
                    }
                }
                double hashPerSec = maxTimestamp > 0 ? totalHashCount / maxTimestamp : 0;
                System.Diagnostics.Debug.WriteLine($"\n★★★ [원본 핑거프린트 생성 직후 해시 개수] ★★★");
                System.Diagnostics.Debug.WriteLine($"  총 엔트리 수: {fingerprints.Count}개");
                System.Diagnostics.Debug.WriteLine($"  총 해시 수 (저장 전): {totalHashCount}개");
                System.Diagnostics.Debug.WriteLine($"  총 길이: {maxTimestamp:F0}초");
                System.Diagnostics.Debug.WriteLine($"  ★ 초당 평균 해시 수: {hashPerSec:F1}개/초");
                System.Diagnostics.Debug.WriteLine($"  (Live 기준: 1400-1700개/초)");
                System.Diagnostics.Debug.WriteLine($"★★★ [원본 핑거프린트 생성 직후 해시 개수 끝] ★★★\n");
            }
            // 최적화: 중간 파일이 있으면 이름만 변경 (파일 복사/저장 불필요)
            if (File.Exists(fingerprintsFilePath) && !File.Exists(outputFilePath))
            {
                // 취소 상태 확인: 파일 이동 전 취소 체크
                if (cancellationToken.IsCancellationRequested)
                {
                    peaksList?.Clear();
                    fingerprints?.Clear();
                    return new OriginalFptResult
                    {
                        TotalFingerprints = 0,
                        OutputFilePath = outputFilePath,
                        WasCanceled = true
                    };
                }
                
                try
                {
                    if (statusMsgCbk != null)
                    {
                        statusMsgCbk("중간 파일을 최종 파일로 이름 변경 중...");
                    }
                    
                    // 중간 파일을 최종 파일로 이동 (이름 변경)
                    File.Move(fingerprintsFilePath, outputFilePath);
                    
                    if (statusMsgCbk != null)
                    {
                        statusMsgCbk($"파일 저장 완료 (이름 변경): {Path.GetFileName(outputFilePath)}");
                    }
                }
                catch (Exception ex)
                {
                    // 이동 실패 시 일반 저장 방식 사용
                    if (statusMsgCbk != null)
                    {
                        statusMsgCbk($"이름 변경 실패, 일반 저장 방식 사용: {ex.Message}");
                    }
                    
                    // 취소 상태 확인: 저장 전 취소 체크
                    if (cancellationToken.IsCancellationRequested)
                    {
                        peaksList?.Clear();
                        fingerprints?.Clear();
                        return new OriginalFptResult
                        {
                            TotalFingerprints = 0,
                            OutputFilePath = outputFilePath,
                            WasCanceled = true
                        };
                    }
                    
                    // 파일 저장 단계 메시지 전달
                    if (statusMsgCbk != null)
                    {
                        statusMsgCbk("파일 저장 중...");
                    }
                    
                    try
                    {
                        // 파일로 저장 (핑거프린트 생성이 완료된 후에만 실행됨)
                        Save_movieFptsToFile(fingerprints, outputFilePath, context, useQuantization: true, hashOnly: hashOnly, statusMsgCbk: statusMsgCbk);
                        
                        // 저장 성공 확인
                        if (File.Exists(outputFilePath))
                        {
                            if (statusMsgCbk != null)
                            {
                                statusMsgCbk($"파일 저장 완료: {Path.GetFileName(outputFilePath)}");
                            }
                        }
                        else
                        {
                            if (statusMsgCbk != null)
                            {
                                statusMsgCbk($"파일 저장 실패: 파일이 생성되지 않았습니다.");
                            }
                            System.Diagnostics.Debug.WriteLine($"SaveFingerprintsToFile: 파일이 생성되지 않음: {outputFilePath}");
                        }
                    }
                    catch (Exception saveEx)
                    {
                        if (statusMsgCbk != null)
                        {
                            try
                            {
                                statusMsgCbk($"파일 저장 실패: {saveEx.GetType().Name} - {saveEx.Message}");
                            }
                            catch { }
                        }
                        System.Diagnostics.Debug.WriteLine($"SaveFingerprintsToFile 예외: {saveEx}");
                        // 예외를 다시 던지지 않고 계속 진행 (최종 파일이 없어도 중간 파일이 있을 수 있음)
                    }
                }
            }
            else
            {
                // 취소 상태 확인: 저장 전 취소 체크
                if (cancellationToken.IsCancellationRequested)
                {
                    peaksList?.Clear();
                    fingerprints?.Clear();
                    return new OriginalFptResult
                    {
                        TotalFingerprints = 0,
                        OutputFilePath = outputFilePath,
                        WasCanceled = true
                    };
                }
                
                // 중간 파일이 없거나 최종 파일이 이미 있는 경우 일반 저장
                // 파일 저장 단계 메시지 전달
                if (statusMsgCbk != null)
                {
                    statusMsgCbk("파일 저장 중...");
                }
                
                try
                {
                    // 파일로 저장 (핑거프린트 생성이 완료된 후에만 실행됨)
                    Save_movieFptsToFile(fingerprints, outputFilePath, context, useQuantization: true, hashOnly: hashOnly, statusMsgCbk: statusMsgCbk);
                    
                    if (statusMsgCbk != null)
                    {
                        statusMsgCbk($"파일 저장 완료: {Path.GetFileName(outputFilePath)}");
                    }
                }
                catch (Exception saveEx)
                {
                    if (statusMsgCbk != null)
                    {
                        try
                        {
                            statusMsgCbk($"파일 저장 실패: {saveEx.GetType().Name} - {saveEx.Message}");
                        }
                        catch { }
                    }
                    System.Diagnostics.Debug.WriteLine($"SaveFingerprintsToFile 예외: {saveEx}");
                    // 예외를 다시 던지지 않고 계속 진행 (최종 파일이 없어도 중간 파일이 있을 수 있음)
                }
            }

            // 취소 상태 확인: Peak 파일 정리 전 취소 체크
            if (cancellationToken.IsCancellationRequested)
            {
                peaksList?.Clear();
                fingerprints?.Clear();
                return new OriginalFptResult
                {
                    TotalFingerprints = 0,
                    OutputFilePath = outputFilePath,
                    WasCanceled = true
                };
            }

            // 최적화: 최종 파일 생성 완료 후 Peak 파일 삭제 (최종 파일이 있으면 Peak 파일 불필요)
            if (File.Exists(peaksFilePath))
            {
                try
                {
                    File.Delete(peaksFilePath);
                    if (statusMsgCbk != null)
                    {
                        statusMsgCbk($"Peak 중간 파일 정리 완료: {Path.GetFileName(peaksFilePath)}");
                    }
                }
                catch
                {
                    // 삭제 실패해도 무시 (중요하지 않음)
                }
            }

            // 최종 파일 생성 여부 확인
            bool fileSaved = File.Exists(outputFilePath);
            if (!fileSaved && statusMsgCbk != null)
            {
                try
                {
                    statusMsgCbk($"경고: 최종 파일이 생성되지 않았습니다. 중간 파일을 확인해주세요.");
                }
                catch { }
            }
            
            return new OriginalFptResult
            {
                TotalFingerprints = fingerprints?.Count ?? 0,
                OutputFilePath = fileSaved ? outputFilePath : null,
                WasCanceled = cancellationToken.IsCancellationRequested,
                AudioSampleRate = audioSampleRate,
            };
        }

        /// <summary>
        /// 단일 프레임을 처리합니다 (병렬 처리용) - 기존 방식 (호환성 유지).
        /// </summary>
        private static void ProcessFrame(
            PickAudioFpParam param,
            int frameIndex,
            double[] buffer,
            long bufferStartMonoIndex,
            int bufferLength,
            double[] hammingWindow,
            double[] frequencies,
            int sampleRate,
            long totalMonoSamples,
            ConcurrentBag<Peak> peaks)
        {
            // 모노 샘플 기준 시작 위치
            int fftSize = param.fptCfg.FFTSize;
            int hopSize = param.fptCfg.HopSize;
            long monoStartIndex = frameIndex * (long)hopSize;
            long monoEndIndex = monoStartIndex + fftSize;
            
            // 파일 끝 체크
            if (monoStartIndex >= totalMonoSamples)
            {
                return;
            }
            
            // 로컬 배열 할당 (Thread-safe를 위해 각 스레드마다 별도 배열 사용)
            double[] frame = new double[fftSize];
            double[] real = new double[fftSize];
            double[] imag = new double[fftSize];
            int spectrumLength = fftSize / 2;
            double[] magnitudes = new double[spectrumLength];
            
            // 버퍼에서 프레임 추출
            int bufferOffset = (int)(monoStartIndex - bufferStartMonoIndex);
            
            // ★★★ 2026-02-03: 버퍼 경계 제로 패딩 제거 ★★★
            // 문제: 버퍼 경계에서 제로 패딩이 발생하면 FFT 결과가 달라짐
            //       → Live(ExtractPeaksFromSamples)와 다른 Peak 추출
            // 해결: 버퍼 경계에 걸친 프레임은 건너뛰기 (다음 버퍼에서 처리)
            if (bufferOffset + fftSize > bufferLength)
            {
                // 버퍼 경계에 걸친 프레임은 건너뛰기
                // 이 프레임은 다음 버퍼에서 처리됨
                return;
            }
            
            // 버퍼 내에 충분한 데이터가 있음
            Array.Copy(buffer, bufferOffset, frame, 0, fftSize);
            
            // 윈도우 함수 적용
            for (int i = 0; i < fftSize; i++)
            {
                real[i] = frame[i] * hammingWindow[i];
                imag[i] = 0;
            }
            
            // FFT 수행
            FFT(real, imag);
            
            // 스펙트럼 계산
            for (int i = 0; i < spectrumLength; i++)
            {
                double re = real[i];
                double im = imag[i];
                magnitudes[i] = re * re + im * im; // 제곱값 저장
            }
            
            // 시간 계산
            double timeInSeconds = monoStartIndex / (double)sampleRate;

            // 피크 검출 (Thread-safe)
            //DetectPeaksUnified(param.fptCfg, peaks, magnitudes, frequencies, timeInSeconds);
            ImprovedPeakDetection.DetectPeaksAdaptive(peaks, magnitudes, frequencies, timeInSeconds, param.fptCfg);
        } // end of ProcessFrame

        /// <summary>
        /// 통일된 Peak 검출 함수 (원본/실시간 모두 사용) - Thread-safe 버전 (ConcurrentBag 사용)
        /// </summary>
        private static void DetectPeaksUnified(
            FingerprintConfig fptConfig,
            ConcurrentBag<Peak> peaks,
            double[] magnitudes,
            double[] frequencies,
            double time)
        {
            if (magnitudes.Length == 0) return;

            // 통계 계산
            double sum = 0, sumSq = 0, max = 0;
            for (int i = 0; i < magnitudes.Length; i++)
            {
                sum += magnitudes[i];
                sumSq += magnitudes[i] * magnitudes[i];
                if (magnitudes[i] > max) max = magnitudes[i];
            }

            double mean = sum / magnitudes.Length;
            double variance = (sumSq / magnitudes.Length) - (mean * mean);
            double stdDev = Math.Sqrt(Math.Max(0, variance)); // 표준편차

            // 임계값: 평균 + N*표준편차
            double threshold = mean + fptConfig.PeakThresholdMultiplier * stdDev;
            threshold = Math.Max(threshold, max * 0.1);

            // Peak 후보 수집
            var candidates = new List<(int idx, double mag, double freq)>();

            int neighborhood = fptConfig.PeakNeighborhoodSize;
            for (int Ifrq = neighborhood; Ifrq < magnitudes.Length - neighborhood; Ifrq++)
            {
                if (magnitudes[Ifrq] < threshold) continue;
                // 국소 최대값 확인
                bool isLocalMax = true; 
                for (int offset = -neighborhood; offset <= neighborhood && isLocalMax; offset++)
                {
                    if (offset != 0 && magnitudes[Ifrq + offset] >= magnitudes[Ifrq])
                        isLocalMax = false;
                }

                if (isLocalMax)
                    candidates.Add((Ifrq, magnitudes[Ifrq], frequencies[Ifrq]));
            }

            // 상위 N개(강한 peak)만 선택
            foreach (var c in candidates.OrderByDescending(x => x.mag).Take(fptConfig.MaxPeaksPerFrame))
            {
                peaks.Add(new Peak { Time = time, Frequency = c.freq, Magnitude = c.mag });
            }
        }

        /// <summary>
        /// 프레임별 SNR(Signal-to-Noise Ratio) 추정
        /// 상위 10% 주파수 대역 전력(신호 영역)과 하위 20% 주파수 대역 전력(노이즈 영역)을 비교
        /// </summary>
        /// <param name="magnitudes">스펙트럼 magnitude 배열</param>
        /// <returns>dB 단위 SNR 값</returns>
        private static double EstimateSNRFrame(double[] magnitudes)
        {
            if (magnitudes == null || magnitudes.Length == 0)
            {
                return double.NegativeInfinity;
            }

            // 상위 10% 주파수 대역 (신호 영역)
            int signalBins = Math.Max(1, (int)(magnitudes.Length * SignalBandRatio));
            double[] sortedMagnitudes = new double[magnitudes.Length];
            Array.Copy(magnitudes, sortedMagnitudes, magnitudes.Length);
            Array.Sort(sortedMagnitudes);
            
            double signalPower = 0.0;
            for (int i = sortedMagnitudes.Length - signalBins; i < sortedMagnitudes.Length; i++)
            {
                signalPower += sortedMagnitudes[i] * sortedMagnitudes[i];
            }
            signalPower /= signalBins;

            // 하위 20% 주파수 대역 (노이즈 영역)
            int noiseBins = Math.Max(1, (int)(magnitudes.Length * NoiseBandRatio));
            double noisePower = 0.0;
            for (int i = 0; i < noiseBins; i++)
            {
                noisePower += sortedMagnitudes[i] * sortedMagnitudes[i];
            }
            noisePower /= noiseBins;

            // SNR 계산 (divide by zero 방지)
            if (noisePower < 1e-10)
            {
                noisePower = 1e-10;
            }

            double snrDb = 10.0 * Math.Log10(signalPower / noisePower);
            return snrDb;
        }

        /// <summary>
        /// 스펙트럼 특징 계산 (Spectral Centroid, Spectral Entropy, Peak Sharpness)
        /// </summary>
        /// <param name="magnitudes">스펙트럼 magnitude 배열</param>
        /// <param name="frequencies">주파수 배열</param>
        /// <returns>spectralCentroid, spectralEntropy, peakSharpness</returns>
        private static (double spectralCentroid, double spectralEntropy, double peakSharpness) ComputeSpectralFeatures(double[] magnitudes, double[] frequencies)
        {
            if (magnitudes == null || magnitudes.Length == 0 || frequencies == null || frequencies.Length != magnitudes.Length)
            {
                return (0.0, 0.0, 0.0);
            }

            double sumMagnitude = 0.0;
            for (int i = 0; i < magnitudes.Length; i++)
            {
                sumMagnitude += magnitudes[i];
            }

            if (sumMagnitude < 1e-10)
            {
                return (0.0, 0.0, 0.0);
            }

            // 정규화
            double[] normalized = new double[magnitudes.Length];
            for (int i = 0; i < magnitudes.Length; i++)
            {
                normalized[i] = magnitudes[i] / sumMagnitude;
            }

            // Spectral Centroid (스펙트럼 무게중심 주파수)
            double spectralCentroid = 0.0;
            for (int i = 0; i < magnitudes.Length; i++)
            {
                spectralCentroid += frequencies[i] * normalized[i];
            }

            // Spectral Entropy (신호 집중도, 낮을수록 신호가 명확함)
            double spectralEntropy = 0.0;
            for (int i = 0; i < normalized.Length; i++)
            {
                double p = normalized[i];
                if (p > 1e-10)
                {
                    spectralEntropy -= p * Math.Log(p + 1e-10, 2.0);
                }
            }

            // Peak Sharpness (peak의 선명도, 도함수의 변화율로 측정)
            double peakSharpness = 0.0;
            if (magnitudes.Length > 1)
            {
                for (int i = 1; i < magnitudes.Length; i++)
                {
                    peakSharpness += Math.Abs(magnitudes[i] - magnitudes[i - 1]);
                }
                peakSharpness /= (magnitudes.Length - 1);
            }

            return (spectralCentroid, spectralEntropy, peakSharpness);
        }

        /// <summary>
        /// 품질 점수 계산 (0-100)
        /// SNR, Spectral Entropy, Peak Sharpness, Peak 개수를 종합하여 평가
        /// </summary>
        /// <param name="peakCount">Peak 개수</param>
        /// <param name="snrDb">SNR (dB)</param>
        /// <param name="spectralEntropy">Spectral Entropy</param>
        /// <param name="peakSharpness">Peak Sharpness</param>
        /// <returns>품질 점수 (0-100)</returns>
        private static double CalculateQualityScore(int peakCount, double snrDb, double spectralEntropy, double peakSharpness)
        {
            // 정규화된 메트릭들 (0-1)
            double peakNorm = Math.Max(0.0, Math.Min(1.0, peakCount / 50.0)); // 50개 이상이면 max
            double snrNorm = Math.Max(0.0, Math.Min(1.0, (snrDb + 10.0) / 30.0)); // -10~20dB 범위
            double entropyNorm = 1.0 - Math.Max(0.0, Math.Min(1.0, spectralEntropy / 5.0)); // 낮을수록 좋음
            double sharpnessNorm = Math.Max(0.0, Math.Min(1.0, peakSharpness / 100.0));

            // 프레임 단위 평가 시 peak 개수가 0이면 peak 가중치를 제거하고 나머지 메트릭만 사용
            double quality;
            if (peakCount == 0)
            {
                // Peak 개수 없이 평가 (SNR, Spectral 특징만 사용)
                quality = (
                    0.40 * snrNorm +         // 40% - SNR
                    0.35 * entropyNorm +     // 35% - 스펙트럼 집중도
                    0.25 * sharpnessNorm     // 25% - peak 선명도
                ) * 100.0;
            }
            else
            {
                // 가중 평균 (윈도우 단위 평가)
                quality = (
                    0.35 * peakNorm +        // 35% - peak 개수
                    0.30 * snrNorm +         // 30% - SNR
                    0.20 * entropyNorm +     // 20% - 스펙트럼 집중도
                    0.15 * sharpnessNorm     // 15% - peak 선명도
                ) * 100.0;
            }

            return quality;
        }

     
        /// <summary>
        /// 동적 시간 윈도우 계산 (원본과 pick 모두에 적용)
        /// ★ 2026.01.25 수정: 동적 시간 윈도우 비활성화 ★
        /// 라이브 매칭에서 동적 시간 윈도우가 원본과 다른 Peak 쌍을 선택하는 문제 해결
        /// 항상 고정된 HashTimeWindow (3초)를 사용하여 일관성 유지
        /// </summary>
        /// <param name="peakCount">현재 시간 윈도우 내의 Peak 수 (현재 미사용)</param>
        /// <returns>고정 시간 윈도우 (HashTimeWindow, 기본 3초)</returns>
        private static int CalculateDynamicTimeWindow(int peakCount)
        {
            // ★ 동적 시간 윈도우 비활성화: 항상 기본값 사용 ★
            // 이전 동적 로직은 라이브 핑거프린트에서 원본과 다른 Peak 쌍을 선택하여
            // 매칭률이 매우 낮아지는 문제를 일으켰음
            // 이 수정 후 핑거프린트 파일 재생성 필요!
            return HashTimeWindow; // 기본값 3초 (고정)
            
            /* 이전 동적 로직 (비활성화):
            if (peakCount < 15)
            {
                return HashTimeWindow + 3; // 6초 (매우 적은 경우)
            }
            else if (peakCount < 20)
            {
                return HashTimeWindow + 2; // 5초
            }
            else if (peakCount < 25)
            {
                return HashTimeWindow + 1; // 4초
            }
            return HashTimeWindow; // 기본값 3초
            */
        }
        
        /// <summary>
        /// Peak 밀도 기반 동적 Fan-out 계산
        /// ★ 2026.01.28 재활성화: 초당 일정한 해시 수 생성을 목표로 동적 조절 ★
        /// Peak 정렬이 완전 결정적(시간→주파수→Magnitude)으로 변경되어 일관성 보장
        /// </summary>
        /// <param name="windowPeakCount">시간 윈도우 내 peak 수</param>
        /// <returns>동적 Fan-out 값 (5-15 범위)</returns>
        
        // ★★★ 진단용 통계 변수 ★★★
        private static long _diagFanOutCallCount = 0;
        private static long _diagTotalWindowPeakCount = 0;
        private static long _diagTotalDynamicFanOut = 0;
        private static int _diagMinWindowPeakCount = int.MaxValue;
        private static int _diagMaxWindowPeakCount = 0;
        
        public static void ResetFanOutDiagnostics()
        {
            _diagFanOutCallCount = 0;
            _diagTotalWindowPeakCount = 0;
            _diagTotalDynamicFanOut = 0;
            _diagMinWindowPeakCount = int.MaxValue;
            _diagMaxWindowPeakCount = 0;
        }
        
        public static void PrintFanOutDiagnostics()
        {
            if (_diagFanOutCallCount > 0)
            {
                double avgWindowPeakCount = _diagTotalWindowPeakCount / (double)_diagFanOutCallCount;
                double avgDynamicFanOut = _diagTotalDynamicFanOut / (double)_diagFanOutCallCount;
                System.Diagnostics.Debug.WriteLine($"\n★★★ [Dynamic Fan-out 통계] ★★★");
                System.Diagnostics.Debug.WriteLine($"  호출 횟수: {_diagFanOutCallCount}회");
                System.Diagnostics.Debug.WriteLine($"  windowPeakCount: 평균={avgWindowPeakCount:F1}, 최소={_diagMinWindowPeakCount}, 최대={_diagMaxWindowPeakCount}");
                System.Diagnostics.Debug.WriteLine($"  dynamicFanOut: 평균={avgDynamicFanOut:F2}");
                System.Diagnostics.Debug.WriteLine($"  예상 해시/Peak: {avgDynamicFanOut:F1}");
                System.Diagnostics.Debug.WriteLine($"★★★ [Dynamic Fan-out 통계 끝] ★★★\n");
            }
        }
        
        private static int CalculateDynamicFanOut(int windowPeakCount)
        {
            // 목표: 초당 약 100개 해시 생성 (일정하게 유지)
            // 해시 수 ≈ peaks × fanOut → fanOut = target / peakDensity
            const double TargetHashesPerSecond = 100.0;
            const int MinFanOut = 5;
            const int MaxFanOut = 15;
            
            // 윈도우 내 Peak 밀도 계산 (HashTimeWindow = 3초 기준)
            double peakDensity = windowPeakCount / (double)HashTimeWindow;
            
            if (peakDensity <= 0)
            {
                return MaxFanOut; // Peak가 거의 없으면 최대 Fan-out
            }
            
            // 목표 해시 수를 기반으로 Fan-out 계산
            int dynamicFanOut = (int)Math.Round(TargetHashesPerSecond / peakDensity);
            
            // 범위 제한 (5-15)
            dynamicFanOut = Math.Max(MinFanOut, Math.Min(MaxFanOut, dynamicFanOut));
            
            // 실제 peak 수를 초과하지 않도록 제한
            int result = Math.Min(dynamicFanOut, windowPeakCount);
            
            // ★★★ 진단 통계 수집 ★★★
            System.Threading.Interlocked.Increment(ref _diagFanOutCallCount);
            System.Threading.Interlocked.Add(ref _diagTotalWindowPeakCount, windowPeakCount);
            System.Threading.Interlocked.Add(ref _diagTotalDynamicFanOut, result);
            if (windowPeakCount < _diagMinWindowPeakCount) _diagMinWindowPeakCount = windowPeakCount;
            if (windowPeakCount > _diagMaxWindowPeakCount) _diagMaxWindowPeakCount = windowPeakCount;
            
            return result;
        }

        /// <summary>
        /// Peak 쌍 생성을 위한 반복 파라미터를 계산합니다.
        /// 5곳에서 공통으로 사용되는 로직을 통합하여 일관성을 보장합니다.
        /// </summary>
        /// <param name="currentIndex">현재 peak의 인덱스 (i)</param>
        /// <param name="foundIndex">이진 검색으로 찾은 윈도우 끝 인덱스</param>
        /// <param name="windowPeakCount">윈도우 내 peak 수</param>
        /// <param name="dynamicFanOut">동적 Fan-out 값</param>
        /// <returns>(maxIterations: 최대 반복 횟수, step: 샘플링 간격, actualEndIndex: 실제 종료 인덱스)</returns>
        private static (int maxIterations, int step, int actualEndIndex) CalculatePeakPairIterationParams(
            int currentIndex,
            int foundIndex,
            int windowPeakCount,
            int dynamicFanOut)
        {
            int actualEndIndex = foundIndex;
            int step = 1;

            if (windowPeakCount > dynamicFanOut)
            {
                // 위치 5(청크 처리)의 안전장치 통합: step이 0이 되지 않도록 보장
                step = Math.Max(1, windowPeakCount / dynamicFanOut);
                actualEndIndex = currentIndex + 1 + dynamicFanOut * step;
                if (actualEndIndex > foundIndex) actualEndIndex = foundIndex;
            }

            // 최대 반복 횟수 제한 (안전장치)
            int maxIterations = Math.Min(actualEndIndex - (currentIndex + 1), dynamicFanOut * 2);

            return (maxIterations, step, actualEndIndex);
        }

        /// <summary>
        /// 피크 리스트로부터 핑거프린트를 생성합니다.
        /// Phase 1 최적화: 청크 단위 스트리밍 처리로 메모리 사용량 대폭 감소
        /// 최적화: 시간 윈도우 기반 인덱싱으로 O(n²) 복잡도 개선, LINQ 제거
        /// Peak 밀도 기반 동적 Fan-out 최적화 적용
        /// </summary>
        /// <param name="peaks">Peak 리스트</param>
        /// <param name="sampleRate">샘플 레이트</param>
        /// <param name="progress">진행률 (선택적)</param>
        /// <param name="statusMsgCbk">상태 메시지 콜백 (선택적)</param>
        /// <param name="useDynamicTimeWindow">동적 시간 윈도우 사용 여부 (기본 true, 라이브 매칭 시 false 권장)</param>
        /// <param name="forIndexing">★ 원본 인덱싱용=true (변형 해시 포함), Live 조회용=false (단일 해시) ★</param>
        private static List<FptEntry> GenerateFingerprints(List<Peak> peaks, int sampleRate, IProgress<OriginalFPProgress> progress = null, Action<string> statusMsgCbk = null, bool useDynamicTimeWindow = true, bool forIndexing = true)
        {
            if (peaks.Count == 0)
            {
                return new List<FptEntry>();
            }

            // 병렬 처리로 인해 시간순 정렬이 보장되지 않으므로 정렬 수행
            // ★★★ 2026-02-05: 시간 우선 정렬 복원 (시간 윈도우 알고리즘 필수) ★★★
            // 주파수 우선 정렬은 시간 윈도우 검색 로직을 깨뜨림
            // Loopback 주파수 시프트는 변형 해시(variation hashes)로 흡수
            peaks.Sort((p1, p2) =>
            {
                int cmp = p1.Time.CompareTo(p2.Time);           // 1차: 시간
                if (cmp != 0) return cmp;
                cmp = p1.Frequency.CompareTo(p2.Frequency);     // 2차: 주파수
                if (cmp != 0) return cmp;
                return p1.Magnitude.CompareTo(p2.Magnitude);    // 3차: Magnitude
            });
            
            // ★★★ 2026-02-06: 원본/Live 일관성을 위한 Peak 필터링 (상위 60%) ★★★
            // 효과: FPT 용량 ~40% 감소, Live와 동일한 해시 생성
            int originalPeakCount = peaks.Count;
            if (peaks.Count > 10)
            {
                const double peakFilterRatio = 0.6; // 상위 60%
                int targetCount = Math.Max(10, (int)(peaks.Count * peakFilterRatio));
                peaks = peaks.OrderByDescending(p => p.Magnitude).Take(targetCount).ToList();
                
                // 시간순 재정렬 (해시 생성에 필수)
                peaks = peaks.OrderBy(p => p.Time).ThenBy(p => p.Frequency).ThenBy(p => p.Magnitude).ToList();
                
                System.Diagnostics.Debug.WriteLine($"[원본 FPT] Peak 필터링: {originalPeakCount}개 → {peaks.Count}개 (상위 60%)");
            }
            
            // Phase 1: 청크 단위 스트리밍 처리 적용
            // 구조 개선: 메모리 부족 방지를 위해 청크 처리 임계값을 낮춤
            // 대용량 데이터(50만 peak 이상)의 경우 청크 단위로 처리
            const int chunkProcessingThreshold = 500000; // 50만 개 이상이면 청크 처리 (100만에서 감소)
            
            // 메모리 상태 확인: 사용 가능한 메모리가 적으면 더 낮은 임계값 사용
            long availableMemoryCheck = GC.GetTotalMemory(false);
            long memoryThreshold = 100 * 1024 * 1024; // 100MB
            int adaptiveThreshold = chunkProcessingThreshold;
            
            if (availableMemoryCheck < memoryThreshold)
            {
                // 메모리가 부족하면 더 낮은 임계값 사용
                adaptiveThreshold = 100000; // 10만 개
            }
            else if (availableMemoryCheck < memoryThreshold * 2)
            {
                // 메모리가 중간 정도면 중간 임계값 사용
                adaptiveThreshold = 250000; // 25만 개
            }
            
            if (peaks.Count >= adaptiveThreshold)
            {
                try
                {
                    // 메모리 정리 후 청크 처리
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                    GC.WaitForPendingFinalizers();
                    return GenerateFingerprintsChunked(peaks, sampleRate, progress, statusMsgCbk);
                }
                catch (OutOfMemoryException)
                {
                    // 청크 처리도 실패하면 순차 처리로 폴백
                    if (statusMsgCbk != null)
                    {
                        try
                        {
                            statusMsgCbk("청크 처리 실패. 순차 처리로 전환합니다...");
                        }
                        catch { }
                    }
                    
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                    GC.WaitForPendingFinalizers();
                    
                    int estimatedHashPairsFallback = Math.Min(peaks.Count * MaxPeaksPerWindow / 10, 500000); // 더 작은 추정값
                    return GenerateFingerprintsSequential(peaks, estimatedHashPairsFallback, progress, statusMsgCbk);
                }
            }
            
            // 소규모 데이터는 기존 방식 사용 (오버헤드 최소화)
            // Phase 2: Thread-local 해시 테이블 적용 (lock 경합 제거)

            // Phase 2: Thread-local 해시 테이블 사용
            // 각 스레드가 독립적인 해시 테이블을 사용하여 lock 경합 완전 제거
            // 메모리 기반 스레드 수 계산: 안정적인 메모리 사용량을 기준으로 스레드 수 결정
            int estimatedHashPairs = Math.Min(peaks.Count * MaxPeaksPerWindow / 10, 1000000);
            
            // 메모리 기반 스레드 수 계산
            // 1. 사용 가능한 메모리 확인
            long availableMemory = GC.GetTotalMemory(false);
            
            // 2. 안정적인 메모리 예산 내에서 사용 가능한 스레드 수 계산
            // 스레드당 필요한 메모리: 스레드 스택(1MB) + ThreadLocal Dictionary 오버헤드(약 600KB)
            long memoryPerThread = MaxMemoryPerThreadBytes;
            
            // 3. 안정적인 메모리 예산을 기준으로 최대 스레드 수 계산
            // 사용 가능한 메모리와 안정적인 예산 중 작은 값을 사용
            long usableMemory = Math.Min(availableMemory, StableMemoryBudgetBytes);
            
            // 4. 메모리 기반 최대 스레드 수 계산 (안전 마진 70% 포함 - 매우 보수적)
            int maxThreadsByMemory = (int)((usableMemory * 0.3) / memoryPerThread);
            
            // 5. CPU 코어 수와 비교하여 최종 스레드 수 결정
            int maxThreads = Math.Min(Environment.ProcessorCount, Math.Max(1, maxThreadsByMemory));
            
            // 6. 최소 1개, 최대는 하드 리미트로 제한 (Thread.cs OutOfMemoryException 방지)
            maxThreads = Math.Max(1, Math.Min(maxThreads, MaxThreadsHardLimit));
            
            // 7. ThreadLocal 초기 용량 계산 (스레드 수에 따라 조정, 더 보수적으로)
            int initialCapacity = Math.Min(estimatedHashPairs / Math.Max(maxThreads, 1), 10000);
            
            // 8. 최종 메모리 체크: 실제 필요한 메모리와 사용 가능한 메모리 비교
            // Thread.cs GetCurrentThreadNative() 크래시 방지: ThreadLocal 사용을 더욱 보수적으로 제한
            // ThreadLocal은 Thread.CurrentThread를 호출하므로 메모리가 충분할 때만 사용
            long estimatedMemoryNeeded = (long)maxThreads * memoryPerThread;
            // 5배 이상 여유 필요 (이전 3배에서 증가) - Thread.CurrentThread 호출 안정성 확보
            bool useThreadLocal = availableMemory >= estimatedMemoryNeeded * 5.0;
            
            if (!useThreadLocal)
            {
                if (statusMsgCbk != null)
                {
                    try
                    {
                        statusMsgCbk($"메모리 부족으로 기본 모드로 전환합니다... (사용 가능: {availableMemory / 1024 / 1024}MB, 필요: {estimatedMemoryNeeded / 1024 / 1024}MB)");
                    }
                    catch { }
                }
                // ThreadLocal 없이 기본 방식 사용
                // 기본 모드에서는 하드 리미트 적용
                maxThreads = Math.Min(Environment.ProcessorCount, MaxThreadsHardLimit);
            }
            else
            {
                if (statusMsgCbk != null && maxThreads > 1)
                {
                    try
                    {
                        statusMsgCbk($"메모리 기반 스레드 수: {maxThreads}개 (사용 가능 메모리: {availableMemory / 1024 / 1024}MB)");
                    }
                    catch { }
                }
            }
            
            // ★★★ 64비트 해시 적용: uint → ulong ★★★
            ThreadLocal<Dictionary<ulong, List<FingerprintHash>>> threadLocalHashTable = null;
            
            if (useThreadLocal)
            {
                try
                {
                    // Thread.cs GetCurrentThreadNative() 크래시 방지: ThreadLocal 생성 전에 강력한 메모리 정리
                    // GC를 여러 번 실행하여 Thread.CurrentThread 호출 시 메모리 부족 방지
                    for (int gcIteration = 0; gcIteration < 3; gcIteration++)
                    {
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                        GC.WaitForPendingFinalizers();
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                    }
                    
                    // 메모리 재확인 - GC 후에도 충분한 메모리가 있는지 확인
                    long memoryAfterGC = GC.GetTotalMemory(false);
                    if (memoryAfterGC < estimatedMemoryNeeded * 5.0)
                    {
                        // GC 후에도 메모리가 부족하면 ThreadLocal 사용 안 함
                        useThreadLocal = false;
                        if (statusMsgCbk != null)
                        {
                            try
                            {
                                statusMsgCbk("GC 후 메모리 부족으로 기본 모드로 전환합니다...");
                            }
                            catch { }
                        }
                    }
                    
                    if (useThreadLocal)
                    {
                        // Thread.cs GetCurrentThreadNative() 크래시 방지: ThreadLocal 초기화 함수를 안전하게 처리
                        // ThreadLocal 초기화 시 Thread.CurrentThread가 호출되므로 매우 조심스럽게 처리
                        threadLocalHashTable = new ThreadLocal<Dictionary<ulong, List<FingerprintHash>>>(() => 
                        {
                            try
                            {
                                // Thread.CurrentThread 호출을 최소화하기 위해 초기 용량을 매우 작게 설정
                                // ThreadLocal 초기화 함수 내에서 Thread.CurrentThread가 호출되므로 최소한의 작업만 수행
                                int safeCapacity = Math.Min(initialCapacity, 2000); // 5000에서 2000으로 감소
                                return new Dictionary<ulong, List<FingerprintHash>>(safeCapacity);
                            }
                            catch (OutOfMemoryException)
                            {
                                // 메모리 부족 시 최소 용량으로 생성
                                return new Dictionary<ulong, List<FingerprintHash>>(100);
                            }
                            catch
                            {
                                // 기타 예외도 최소 용량으로 생성
                                return new Dictionary<ulong, List<FingerprintHash>>(100);
                            }
                        }, trackAllValues: true);
                    }
                }
                catch (OutOfMemoryException)
                {
                    // ThreadLocal 생성 실패 시 즉시 폴백 (재시도 안 함)
                    useThreadLocal = false;
                    if (statusMsgCbk != null)
                    {
                        try
                        {
                            statusMsgCbk("ThreadLocal 생성 실패로 기본 모드로 전환합니다...");
                        }
                        catch { }
                    }
                    // 메모리 정리
                    try
                    {
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                    }
                    catch { }
                }
                catch
                {
                    // 기타 예외도 폴백
                    useThreadLocal = false;
                }
            }
            
            if (!useThreadLocal)
            {
                // ThreadLocal 사용 안 함 - 기본 방식으로 폴백
                // 메모리 부족으로 ThreadLocal 사용 불가, 기본 방식으로 처리
                if (statusMsgCbk != null)
                {
                    statusMsgCbk("메모리 부족으로 기본 모드로 전환합니다...");
                }
                    
                // 기본 방식: ConcurrentDictionary 사용 (메모리 기반 스레드 수 계산)
                int estimatedHashPairsFallback = Math.Min(peaks.Count * MaxPeaksPerWindow / 10, 100000);
                    
                // 메모리 기반 스레드 수 계산 (ThreadLocal 없으므로 스레드 스택만 고려)
                long availableMemoryFallback = GC.GetTotalMemory(false);
                long usableMemoryFallback = Math.Min(availableMemoryFallback, StableMemoryBudgetBytes);
                int fallbackThreadsByMemory = (int)((usableMemoryFallback * 0.3) / ThreadStackSizeBytes); // 70% 안전 마진
                int fallbackThreads = Math.Min(Environment.ProcessorCount, Math.Max(1, Math.Min(fallbackThreadsByMemory, MaxThreadsHardLimit)));
                    
                var hashTableFallback = new ConcurrentDictionary<ulong, List<FingerprintHash>>(
                    fallbackThreads,
                    estimatedHashPairsFallback);

                int processedPeaksFallback = 0;
                int totalHashPairsFallback = 0;
                object progressLockFallback = new object();
                int lastReportedPercentFallback = -1;
                DateTime startTimeFallback = DateTime.Now;

                Parallel.For(0, peaks.Count, new ParallelOptions
                {
                    MaxDegreeOfParallelism = fallbackThreads
                }, (i) =>
                {
                    var peak1 = peaks[i];
                    if (peak1 == null || double.IsNaN(peak1.Time) || double.IsInfinity(peak1.Time) ||
                        double.IsNaN(peak1.Frequency) || double.IsInfinity(peak1.Frequency))
                    {
                        Interlocked.Increment(ref processedPeaksFallback);
                        return;
                    }
                        
                    double windowEnd = peak1.Time + HashTimeWindow;
                    int foundIndex = BinarySearchPeakIndex(peaks, windowEnd);
                    int windowPeakCount = foundIndex - (i + 1);
                        
                    // Peak 밀도 기반 동적 Fan-out 계산
                    int dynamicFanOut = CalculateDynamicFanOut(windowPeakCount);
                    
                    // ★ 위치 1: Parallel.For fallback 병렬 처리 ★
                    // 특이사항: 기본 HashTimeWindow만 사용, ConcurrentDictionary + lock으로 해시 저장
                    var (maxInnerIterations, step, actualEndIndex) = CalculatePeakPairIterationParams(i, foundIndex, windowPeakCount, dynamicFanOut);
                    int innerIterationCount = 0;
                    
                    int processedPairs = 0;
                    for (int j = i + 1; j < actualEndIndex && innerIterationCount < maxInnerIterations; j++)
                    {
                        innerIterationCount++;
                        
                        if (windowPeakCount > dynamicFanOut && (j - (i + 1)) % step != 0 && j != actualEndIndex - 1)
                            continue;
                            
                        var peak2 = peaks[j];
                        if (peak2 == null || double.IsNaN(peak2.Time) || double.IsInfinity(peak2.Time) ||
                            double.IsNaN(peak2.Frequency) || double.IsInfinity(peak2.Frequency) || peak2.Time > windowEnd)
                        {
                            if (peak2.Time > windowEnd) break;
                            continue;
                        }
                            
                        processedPairs++;
                        List<ulong> hashes;
                        try { 
                            //hashes = GenerateMultipleHashes(peak1, peak2); 
                            // ★ forIndexing 파라미터 전달: 원본=변형 포함, Live=단일 ★
                            hashes = ImprovedHashGeneration.GenerateRobustHashes64(peak1, peak2, forIndexing);
                        }
                        catch { continue; }

                        double timeDelta = peak2.Time - peak1.Time;
                        // ★★★ 2026-02-07: 같은 프레임 Peak 건너뛰기 (dt=0 문제 해결) ★★★
                        if (double.IsNaN(timeDelta) || double.IsInfinity(timeDelta) || timeDelta < MinTimeDeltaForHash) continue;
                            
                        try
                        {
                            foreach (var hash in hashes)
                            {
                                var hashList = hashTableFallback.GetOrAdd(hash, _ => new List<FingerprintHash>(4));
                                lock (hashList)
                                {
                                    hashList.Add(new FingerprintHash
                                    {
                                        Time = peak1.Time,
                                        Frequency1 = peak1.Frequency,
                                        Frequency2 = peak2.Frequency,
                                        TimeDelta = timeDelta
                                    });
                                }
                            }
                        }
                        catch { continue; }
                            
                        // Combinatorial Hashing: Triplet (3개 peak 조합) 생성
                        if (UseCombinatorialHashing && j + CombinatorialHashingStep < actualEndIndex)
                        {
                            int k = j + CombinatorialHashingStep;
                            if (k < actualEndIndex && k < peaks.Count)
                            {
                                var peak3 = peaks[k];
                                if (peak3 != null && !double.IsNaN(peak3.Time) && !double.IsInfinity(peak3.Time) &&
                                    !double.IsNaN(peak3.Frequency) && !double.IsInfinity(peak3.Frequency) &&
                                    peak3.Time <= windowEnd)
                                {
                                    try
                                    {
                                        ulong tripletHash = GeneratePeakTripletHash64(peak1, peak2, peak3);
                                        var tripletHashList = hashTableFallback.GetOrAdd(tripletHash, _ => new List<FingerprintHash>(4));
                                        double timeDelta13 = peak3.Time - peak1.Time;
                                        if (!double.IsNaN(timeDelta13) && !double.IsInfinity(timeDelta13))
                                        {
                                            lock (tripletHashList)
                                            {
                                                tripletHashList.Add(new FingerprintHash
                                                {
                                                    Time = peak1.Time,
                                                    Frequency1 = peak1.Frequency,
                                                    Frequency2 = peak3.Frequency,
                                                    TimeDelta = timeDelta13
                                                });
                                            }
                                        }
                                    }
                                    catch { continue; }
                                }
                            }
                        }
                    }
                        
                    int newCount = Interlocked.Increment(ref processedPeaksFallback);
                    Interlocked.Add(ref totalHashPairsFallback, processedPairs);
                    ReportProgress(newCount, peaks.Count, totalHashPairsFallback, statusMsgCbk, progressLockFallback, ref lastReportedPercentFallback, startTimeFallback);
                });

                // 타임스탬프별 그룹화
                // null 체크: ArgumentNullException 방지
                int estimatedTimestampsFallback = 100;
                if (peaks.Count > 0)
                {
                    var firstPeak = peaks[0];
                    var lastPeak = peaks[peaks.Count - 1];
                    if (firstPeak != null && lastPeak != null)
                    {
                        // ★★★ 2026-02-05: 주파수 우선 정렬로 시간 순서가 보장되지 않음 ★★★
                        estimatedTimestampsFallback = Math.Max(1, (int)Math.Ceiling(Math.Abs(lastPeak.Time - firstPeak.Time)) + 1);
                    }
                }
                var timestampMapFallback = new Dictionary<int, Dictionary<string, FingerprintHashData>>(estimatedTimestampsFallback);

                foreach (var kvp in hashTableFallback)
                {
                    string hashKey = kvp.Key.ToString("X16");
                    foreach (var hash in kvp.Value)
                    {
                        int timestamp = (int)Math.Floor(hash.Time);
                        if (!timestampMapFallback.TryGetValue(timestamp, out var hashSet))
                        {
                            hashSet = new Dictionary<string, FingerprintHashData>();
                            timestampMapFallback[timestamp] = hashSet;
                        }
                        if (!hashSet.ContainsKey(hashKey))
                        {
                            hashSet[hashKey] = new FingerprintHashData
                            {
                                Hash = hashKey,
                                Frequency1 = hash.Frequency1,
                                Frequency2 = hash.Frequency2,
                                TimeDelta = hash.TimeDelta,
                                TimeMs = (int)(hash.Time * 1000) // ★ TimeMs 설정 추가 ★
                            };
                        }
                    }
                }

                var timestampsFallback = new List<int>(timestampMapFallback.Keys);
                timestampsFallback.Sort();
                var fingerprintsFallback = new List<FptEntry>(timestampsFallback.Count);
                foreach (int timestamp in timestampsFallback)
                {
                    var hashDict = timestampMapFallback[timestamp];
                    if (hashDict.Count > 0)
                    {
                        fingerprintsFallback.Add(new FptEntry
                        {
                            Timestamp = timestamp,
                            Hashes = new List<FingerprintHashData>(hashDict.Values)
                        });
                    }
                }
                return fingerprintsFallback;
            }

            // ThreadLocal 사용하는 경우의 처리
            // 진행 상황 추적 (Thread-safe)
            int processedPeaks = 0;
            int totalHashPairs = 0; // 생성된 해시 쌍 수 추적
            object progressLock = new object();
            int lastReportedPercent = -1;
            DateTime startTime = DateTime.Now; // 시작 시간 추적

            // 병렬 처리: 외부 루프를 병렬화하여 다중 CPU 코어 활용
            // Phase 2: 각 스레드는 독립적인 해시 테이블 사용 (lock 없음)
            // 근본적인 메모리 부족 방지: 스레드 수를 더욱 제한하고 순차 처리 폴백 준비
            try
            {
                // 스레드 수가 1개 이하면 순차 처리로 전환
                if (maxThreads <= 1)
                {
                    // 순차 처리로 폴백 (위의 폴백 코드와 동일한 로직)
                    if (statusMsgCbk != null)
                    {
                        statusMsgCbk("메모리 부족으로 순차 처리 모드로 전환합니다...");
                    }
                    
                    var hashTableSequential = new Dictionary<ulong, List<FingerprintHash>>(estimatedHashPairs);
                    
                    for (int i = 0; i < peaks.Count; i++)
                    {
                        var peak1 = peaks[i];
                        if (peak1 == null || double.IsNaN(peak1.Time) || double.IsInfinity(peak1.Time) ||
                            double.IsNaN(peak1.Frequency) || double.IsInfinity(peak1.Frequency))
                        {
                            continue;
                        }
                        
                        // 먼저 기본 시간 윈도우로 windowPeakCount 추정
                        double windowEnd = peak1.Time + HashTimeWindow;
                        int foundIndex = BinarySearchPeakIndex(peaks, windowEnd);
                        int windowPeakCount = foundIndex - (i + 1);
                        
                        // 동적 시간 윈도우 계산
                        int dynamicTimeWindow = CalculateDynamicTimeWindow(windowPeakCount);
                        // 동적 시간 윈도우가 기본값과 다르면 windowEnd 재계산
                        if (dynamicTimeWindow != HashTimeWindow)
                        {
                            windowEnd = peak1.Time + dynamicTimeWindow;
                            foundIndex = BinarySearchPeakIndex(peaks, windowEnd);
                            windowPeakCount = foundIndex - (i + 1);
                        }
                        
                        // Peak 밀도 기반 동적 Fan-out 계산
                        int dynamicFanOut = CalculateDynamicFanOut(windowPeakCount);
                        
                        // ★ 위치 2: 순차 처리 (maxThreads <= 1 조건 분기) ★
                        // 특이사항: 동적 시간 윈도우(CalculateDynamicTimeWindow) 적용, Dictionary로 해시 저장
                        var (maxInnerIterations, step, actualEndIndex) = CalculatePeakPairIterationParams(i, foundIndex, windowPeakCount, dynamicFanOut);
                        int innerIterationCount = 0;

                        for (int j = i + 1; j < actualEndIndex && innerIterationCount < maxInnerIterations; j++)
                        {
                            innerIterationCount++;
                            
                            if (windowPeakCount > dynamicFanOut && (j - (i + 1)) % step != 0 && j != actualEndIndex - 1)
                                continue;
                            
                            var peak2 = peaks[j];
                            if (peak2 == null || double.IsNaN(peak2.Time) || double.IsInfinity(peak2.Time) ||
                                double.IsNaN(peak2.Frequency) || double.IsInfinity(peak2.Frequency) || peak2.Time > windowEnd)
                            {
                                if (peak2.Time > windowEnd) break;
                                continue;
                            }
                            
                            List<ulong> hashes;
                            try {
                                //hashes = GenerateMultipleHashes(peak1, peak2); 
                                // ★ forIndexing 파라미터 전달: 원본=변형 포함, Live=단일 ★
                                hashes = ImprovedHashGeneration.GenerateRobustHashes64(peak1, peak2, forIndexing);
                            }
                            catch { continue; }

                            double timeDelta = peak2.Time - peak1.Time;
                            // ★★★ 2026-02-07: 같은 프레임 Peak 건너뛰기 (dt=0 문제 해결) ★★★
                            if (double.IsNaN(timeDelta) || double.IsInfinity(timeDelta) || timeDelta < MinTimeDeltaForHash) continue;
                            
                            try
                            {
                                foreach (var hash in hashes)
                                {
                                    if (!hashTableSequential.TryGetValue(hash, out var hashList))
                                    {
                                        hashList = new List<FingerprintHash>(4);
                                        hashTableSequential[hash] = hashList;
                                    }

                                    hashList.Add(new FingerprintHash
                                    {
                                        Time = peak1.Time,
                                        Frequency1 = peak1.Frequency,
                                        Frequency2 = peak2.Frequency,
                                        TimeDelta = timeDelta
                                    });
                                }
                            }
                            catch { continue; }
                            
                            // Combinatorial Hashing: Triplet (3개 peak 조합) 생성
                            if (UseCombinatorialHashing && j + CombinatorialHashingStep < actualEndIndex)
                            {
                                int k = j + CombinatorialHashingStep;
                                if (k < actualEndIndex && k < peaks.Count)
                                {
                                    var peak3 = peaks[k];
                                    if (peak3 != null && !double.IsNaN(peak3.Time) && !double.IsInfinity(peak3.Time) &&
                                        !double.IsNaN(peak3.Frequency) && !double.IsInfinity(peak3.Frequency) &&
                                        peak3.Time <= windowEnd)
                                    {
                                        try
                                        {
                                            ulong tripletHash = GeneratePeakTripletHash64(peak1, peak2, peak3);
                                            if (!hashTableSequential.TryGetValue(tripletHash, out var tripletHashList))
                                            {
                                                tripletHashList = new List<FingerprintHash>(4);
                                                hashTableSequential[tripletHash] = tripletHashList;
                                            }
                                            double timeDelta13 = peak3.Time - peak1.Time;
                                            if (!double.IsNaN(timeDelta13) && !double.IsInfinity(timeDelta13))
                                            {
                                                tripletHashList.Add(new FingerprintHash
                                                {
                                                    Time = peak1.Time,
                                                    Frequency1 = peak1.Frequency,
                                                    Frequency2 = peak3.Frequency,
                                                    TimeDelta = timeDelta13
                                                });
                                            }
                                        }
                                        catch { continue; }
                                    }
                                }
                            }
                        }
                        
                        // 진행 상황 보고 (1000개마다)
                        if (i % 1000 == 0 && statusMsgCbk != null)
                        {
                            int percent = (int)((double)i / peaks.Count * 100);
                            statusMsgCbk($"핑거프린트 생성 중... (순차 처리: {percent}%)");
                        }
                    }
                    
                    // ★★★ 2026-02-05: 주파수 우선 정렬로 시간 순서가 보장되지 않음 - Math.Abs 사용 ★★★
                    int estimatedTimestampsSeq = peaks.Count > 0 
                        ? Math.Max(1, (int)Math.Ceiling(Math.Abs(peaks[peaks.Count - 1].Time - peaks[0].Time)) + 1) 
                        : 100;
                    var timestampMapSeq = new Dictionary<int, Dictionary<string, FingerprintHashData>>(estimatedTimestampsSeq);

                    foreach (var kvp in hashTableSequential)
                    {
                        string hashKey = kvp.Key.ToString("X16");
                        foreach (var hash in kvp.Value)
                        {
                            int timestamp = (int)Math.Floor(hash.Time);
                            if (!timestampMapSeq.TryGetValue(timestamp, out var hashSet))
                            {
                                hashSet = new Dictionary<string, FingerprintHashData>();
                                timestampMapSeq[timestamp] = hashSet;
                            }
                            if (!hashSet.ContainsKey(hashKey))
                            {
                                hashSet[hashKey] = new FingerprintHashData
                                {
                                    Hash = hashKey,
                                    Frequency1 = hash.Frequency1,
                                    Frequency2 = hash.Frequency2,
                                    TimeDelta = hash.TimeDelta,
                                    TimeMs = (int)(hash.Time * 1000) // ★ TimeMs 설정 추가 ★
                                };
                            }
                        }
                    }

                    var timestampsSeq = new List<int>(timestampMapSeq.Keys);
                    timestampsSeq.Sort();
                    var fingerprintsSeq = new List<FptEntry>(timestampsSeq.Count);
                    foreach (int timestamp in timestampsSeq)
                    {
                        var hashDict = timestampMapSeq[timestamp];
                        if (hashDict.Count > 0)
                        {
                            fingerprintsSeq.Add(new FptEntry
                            {
                                Timestamp = timestamp,
                                Hashes = new List<FingerprintHashData>(hashDict.Values)
                            });
                        }
                    }
                    return fingerprintsSeq;
                }
                
                // Thread.cs GetCurrentThreadNative() 크래시 방지: Parallel.For를 try-catch로 감싸기
                // 병렬 처리 시작 전에 추가 메모리 정리 (Thread.CurrentThread 호출 안정성 확보)
                try
                {
                    // 병렬 처리 시작 전 메모리 정리 (Thread.CurrentThread 호출 시 메모리 부족 방지)
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                    GC.WaitForPendingFinalizers();
                    
                    Parallel.For(0, peaks.Count, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = maxThreads
                    }, (i) =>
                {
                    var peak1 = peaks[i];
                
                // 피크 유효성 검사 (한 번만 체크)
                if (peak1 == null || double.IsNaN(peak1.Time) || double.IsInfinity(peak1.Time) ||
                    double.IsNaN(peak1.Frequency) || double.IsInfinity(peak1.Frequency))
                {
                    // 진행 상황 업데이트 (유효하지 않은 peak도 카운트)
                    int incrementedCount = Interlocked.Increment(ref processedPeaks);
                    ReportProgress(incrementedCount, peaks.Count, totalHashPairs, statusMsgCbk, progressLock, ref lastReportedPercent, startTime);
                    return; // null 또는 유효하지 않은 피크는 건너뜀
                }
                
                // 먼저 기본 시간 윈도우로 windowPeakCount 추정
                double windowEnd = peak1.Time + HashTimeWindow;

                // 이진 검색으로 윈도우 끝 지점 찾기 (O(log n))
                // windowEnd보다 큰 첫 번째 인덱스를 찾음
                // 병렬 처리 시 각 스레드는 독립적으로 이진 검색 수행
                int startSearchIndex = i + 1;
                int endSearchIndex = peaks.Count;
                
                // 이진 검색 구현: windowEnd보다 큰 첫 번째 인덱스 찾기
                int left = startSearchIndex;
                int right = endSearchIndex;
                int foundIndex = endSearchIndex; // 기본값: 끝까지
                
                while (left < right)
                {
                    int mid = (left + right) / 2;
                    if (peaks[mid].Time > windowEnd)
                    {
                        foundIndex = mid;
                        right = mid;
                    }
                    else
                    {
                        left = mid + 1;
                    }
                }

                // 근본적인 최적화: 윈도우 내 peak 수 제한 (O(n²) → O(n*k), k는 제한된 peak 수)
                // 모든 쌍을 생성하지 않고, 대표적인 peak만 선택하여 처리 시간 대폭 단축
                // Peak 밀도 기반 동적 Fan-out 최적화 적용
                int windowPeakCount = foundIndex - (i + 1);
                
                // 동적 시간 윈도우 계산
                int dynamicTimeWindow = CalculateDynamicTimeWindow(windowPeakCount);
                // 동적 시간 윈도우가 기본값과 다르면 windowEnd 재계산
                if (dynamicTimeWindow != HashTimeWindow)
                {
                    windowEnd = peak1.Time + dynamicTimeWindow;
                    // 이진 검색으로 다시 찾기
                    left = startSearchIndex;
                    right = endSearchIndex;
                    foundIndex = endSearchIndex;
                    while (left < right)
                    {
                        int mid = (left + right) / 2;
                        if (peaks[mid].Time > windowEnd)
                        {
                            foundIndex = mid;
                            right = mid;
                        }
                        else
                        {
                            left = mid + 1;
                        }
                    }
                    windowPeakCount = foundIndex - (i + 1);
                }
                
                // Peak 밀도 기반 동적 Fan-out 계산
                int dynamicFanOut = CalculateDynamicFanOut(windowPeakCount);
                
                // ★ 위치 3: Parallel.For 메인 병렬 처리 ★
                // 특이사항: 이진 검색 인라인 구현, ThreadLocal<Dictionary>로 해시 저장
                var (maxInnerIterations, step, actualEndIndex) = CalculatePeakPairIterationParams(i, foundIndex, windowPeakCount, dynamicFanOut);
                int innerIterationCount = 0;

                // 시간 윈도우 내의 다른 피크들과 쌍 생성 (제한된 범위만 처리)
                int processedPairs = 0;
                for (int j = i + 1; j < actualEndIndex && innerIterationCount < maxInnerIterations; j++)
                {
                    innerIterationCount++;
                    
                    // 샘플링: step 간격으로 peak 선택
                    if (windowPeakCount > dynamicFanOut && (j - (i + 1)) % step != 0 && j != actualEndIndex - 1)
                    {
                        continue; // 샘플링 간격에 맞지 않으면 건너뜀
                    }
                    
                    var peak2 = peaks[j];

                    // 피크 유효성 검사 (간소화)
                    if (peak2 == null || double.IsNaN(peak2.Time) || double.IsInfinity(peak2.Time) ||
                        double.IsNaN(peak2.Frequency) || double.IsInfinity(peak2.Frequency))
                    {
                        continue; // null 또는 유효하지 않은 피크는 건너뜀
                    }

                    // 이진 검색으로 이미 범위가 제한되었지만, 안전을 위해 한 번 더 체크
                    if (peak2.Time > windowEnd)
                    {
                        break;
                    }
                    
                    processedPairs++;

                    // 성능 최적화: 정수 해시 생성 (문자열 할당/비교 비용 제거)
                    List<ulong> hashes;
                    try
                    {
                        //hashes = GenerateMultipleHashes(peak1, peak2);
                        // ★ forIndexing 파라미터 전달: 원본=변형 포함, Live=단일 ★
                        hashes = ImprovedHashGeneration.GenerateRobustHashes64(peak1, peak2, forIndexing);
                    }
                    catch (OutOfMemoryException)
                    {
                        // 메모리 부족 예외: 로깅 시 메모리 할당을 최소화
                        try
                        {
                            System.Diagnostics.Debug.WriteLine("Hash generation failed: OutOfMemoryException");
                        }
                        catch { }
                        
                        // GC 강제 실행으로 메모리 정리 시도
                        try
                        {
                            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                        }
                        catch { }
                        
                        continue;
                    }
                    catch
                    {
                        // 기타 예외: 건너뜀
                        continue;
                    }

                    // Phase 2: Thread-local 해시 테이블 사용 (lock 없음!)
                    // 각 스레드는 독립적인 Dictionary를 사용하므로 lock이 필요 없음
                    var localHashTable = threadLocalHashTable.Value;

                    // 성능 최적화: lock 없이 직접 추가 (Thread-local이므로 안전)
                    double timeDelta = peak2.Time - peak1.Time;
                    // ★★★ 2026-02-07: 같은 프레임 Peak 건너뛰기 (dt=0 문제 해결) ★★★
                    if (double.IsNaN(timeDelta) || double.IsInfinity(timeDelta) || timeDelta < MinTimeDeltaForHash)
                    {
                        continue;
                    }
                    
                    try
                    {
                        foreach (var hash in hashes)
                        {
                            if (!localHashTable.TryGetValue(hash, out var hashList))
                            {
                                hashList = new List<FingerprintHash>(4);
                                localHashTable[hash] = hashList;
                            }

                            hashList.Add(new FingerprintHash
                            {
                                Time = peak1.Time,
                                Frequency1 = peak1.Frequency,
                                Frequency2 = peak2.Frequency,
                                TimeDelta = timeDelta
                            });
                        }
                    }
                    catch (OutOfMemoryException)
                    {
                        // 메모리 부족 예외 처리: 메모리 할당 최소화
                        try
                        {
                            System.Diagnostics.Debug.WriteLine("Out of memory when adding hash");
                        }
                        catch { }
                        
                        // GC 강제 실행으로 메모리 정리 시도
                        try
                        {
                            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                        }
                        catch { }
                        
                        continue;
                    }
                    catch
                    {
                        // 기타 예외 처리: 건너뜀
                        continue;
                    }
                }
                
                // 진행 상황 업데이트 (Thread-safe)
                int newCount = Interlocked.Increment(ref processedPeaks);
                int currentHashCount = Interlocked.Add(ref totalHashPairs, processedPairs);
                ReportProgress(newCount, peaks.Count, currentHashCount, statusMsgCbk, progressLock, ref lastReportedPercent, startTime);
                });
                }
                catch (OutOfMemoryException)
                {
                    // Thread.cs GetCurrentThreadNative() 크래시 방지: Parallel.For 내부 스레드 생성 실패 시 폴백
                    if (statusMsgCbk != null)
                    {
                        try
                        {
                            statusMsgCbk("스레드 생성 실패로 순차 처리로 전환합니다...");
                        }
                        catch { }
                    }
                    
                    // ThreadLocal 정리
                    if (threadLocalHashTable != null)
                    {
                        try
                        {
                            threadLocalHashTable.Dispose();
                        }
                        catch { }
                    }
                    
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                    GC.WaitForPendingFinalizers();
                    
                    // 순차 처리로 폴백
                    return GenerateFingerprintsSequential(peaks, estimatedHashPairs, progress, statusMsgCbk);
                }
                catch (Exception ex)
                {
                    // Thread.cs GetCurrentThreadNative() 크래시 등 기타 예외 처리
                    if (statusMsgCbk != null)
                    {
                        try
                        {
                            statusMsgCbk($"병렬 처리 오류 발생: {ex.GetType().Name}. 순차 처리로 전환합니다...");
                        }
                        catch { }
                    }
                    
                    // ThreadLocal 정리
                    if (threadLocalHashTable != null)
                    {
                        try
                        {
                            threadLocalHashTable.Dispose();
                        }
                        catch { }
                    }
                    
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                    GC.WaitForPendingFinalizers();
                    
                    // 순차 처리로 폴백
                    return GenerateFingerprintsSequential(peaks, estimatedHashPairs, progress, statusMsgCbk);
                }
            }
            catch (OutOfMemoryException)
            {
                // 병렬 처리 중 메모리 부족 발생 시 ThreadLocal 정리 후 폴백
                if (threadLocalHashTable != null)
                {
                    try
                    {
                        threadLocalHashTable.Dispose();
                    }
                    catch { }
                }
                
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                GC.WaitForPendingFinalizers();
                
                // 기본 방식으로 폴백 (이미 위에서 구현됨)
                // 여기서는 빈 리스트 반환 (실패 처리)
                if (statusMsgCbk != null)
                {
                    statusMsgCbk("메모리 부족으로 처리 실패");
                }
                return new List<FptEntry>();
            }

            // Phase 2: Thread-local 해시 테이블들을 전역 해시 테이블로 병합
            if (statusMsgCbk != null)
            {
                statusMsgCbk("핑거프린트 생성 중... (Thread-local 테이블 병합)");
            }

            var globalHashTable = new ConcurrentDictionary<ulong, List<FingerprintHash>>(
                Environment.ProcessorCount,
                100000);

            // 모든 Thread-local 해시 테이블 병합
            foreach (var localHashTable in threadLocalHashTable.Values)
            {
                MergeThreadLocalHashTable(localHashTable, globalHashTable);
            }

            // Thread-local 해시 테이블 정리
            threadLocalHashTable.Dispose();

            // 최적화: LINQ 제거하고 직접 구현
            // 타임스탬프별로 그룹화하고 정렬
            // 메모리 최적화: 타임스탬프 개수 추정
            int estimatedTimestamps = peaks.Count > 0 ? (int)Math.Ceiling(peaks[peaks.Count - 1].Time - peaks[0].Time) + 1 : 100;
            var timestampMap = new Dictionary<int, Dictionary<string, FingerprintHashData>>(estimatedTimestamps);

            // 타임스탬프 그룹화 진행 상황 표시
            if (statusMsgCbk != null)
            {
                statusMsgCbk("핑거프린트 생성 중... (타임스탬프 그룹화)");
            }
            
            // 최적화: hashTable의 키를 직접 사용 (해시 재생성 불필요)
            // 성능 최적화: 정수 해시를 문자열로 변환 (최종 단계에서만 수행)
            foreach (var kvp in globalHashTable)
            {
                ulong hashUint = kvp.Key; // ★ 64비트 정수 해시 ★
                string hashKey = hashUint.ToString("X16"); // 문자열 변환 (최종 단계에서만)
                
                foreach (var hash in kvp.Value)
                {
                    int timestamp = (int)Math.Floor(hash.Time);
                    
                    if (!timestampMap.TryGetValue(timestamp, out var hashSet))
                    {
                        hashSet = new Dictionary<string, FingerprintHashData>();
                        timestampMap[timestamp] = hashSet;
                    }

                    // 중복 제거: 이미 해시 키가 있으므로 재생성 불필요
                    if (!hashSet.ContainsKey(hashKey))
                    {
                        hashSet[hashKey] = new FingerprintHashData
                        {
                            Hash = hashKey,
                            Frequency1 = hash.Frequency1,
                            Frequency2 = hash.Frequency2,
                            TimeDelta = hash.TimeDelta,
                            TimeMs = (int)(hash.Time * 1000)  // 밀리초 단위로 저장 (Shazam 방식)
                        };
                    }
                }
            }

            // 타임스탬프별로 정렬하여 FingerprintEntry 생성
            if (statusMsgCbk != null)
            {
                statusMsgCbk("핑거프린트 생성 중... (최종 정리)");
            }
            
            var timestamps = new List<int>(timestampMap.Keys);
            timestamps.Sort();

            var fingerprints = new List<FptEntry>(timestamps.Count);
            foreach (int timestamp in timestamps)
            {
                var hashDict = timestampMap[timestamp];
                if (hashDict.Count > 0)
                {
                    fingerprints.Add(new FptEntry
                    {
                        Timestamp = timestamp,
                        Hashes = new List<FingerprintHashData>(hashDict.Values)
                    });
                }
            }

            return fingerprints;
        }

        /// <summary>
        /// Phase 2: Thread-local 해시 테이블을 전역 해시 테이블로 병합합니다.
        /// </summary>
        private static void MergeThreadLocalHashTable(Dictionary<ulong, List<FingerprintHash>> localHashTable,
            ConcurrentDictionary<ulong, List<FingerprintHash>> globalHashTable)
        {
            foreach (var kvp in localHashTable)
            {
                ulong hash = kvp.Key;  // ★ 64비트 해시 ★
                var localHashList = kvp.Value;

                if (localHashList == null || localHashList.Count == 0)
                {
                    continue;
                }

                try
                {
                    var globalHashList = globalHashTable.GetOrAdd(hash, _ => new List<FingerprintHash>());

                    lock (globalHashList)
                    {
                        try
                        {
                            globalHashList.AddRange(localHashList);
                        }
                        catch (OutOfMemoryException)
                        {
                            // AddRange 실패 시 개별 추가로 시도
                            try
                            {
                                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                                
                                int addedCount = 0;
                                foreach (var hashItem in localHashList)
                                {
                                    try
                                    {
                                        globalHashList.Add(hashItem);
                                        addedCount++;
                                    }
                                    catch (OutOfMemoryException)
                                    {
                                        break;
                                    }
                                    catch
                                    {
                                        continue;
                                    }
                                }
                                
                                if (addedCount < localHashList.Count)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Partial merge: {addedCount}/{localHashList.Count} items added");
                                }
                            }
                            catch
                            {
                                // 병합 실패 시 해당 해시는 건너뜀
                            }
                        }
                        catch
                        {
                            continue;
                        }
                    }
                }
                catch (OutOfMemoryException)
                {
                    try
                    {
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                    }
                    catch { }
                    continue;
                }
                catch
                {
                    continue;
                }
            }
        }

        /// <summary>
        /// Phase 1: 청크 단위 스트리밍 처리로 핑거프린트를 생성합니다.
        /// 구조 개선: 순차 처리 + 즉시 타임스탬프 해제 + 메모리 모니터링
        /// 메모리 부족 문제 해결을 위한 근본적인 구조 개선
        /// </summary>
        private static List<FptEntry> GenerateFingerprintsChunked(List<Peak> peaks, int sampleRate, IProgress<OriginalFPProgress> progress = null, Action<string> statusMsgCbk = null)
        {
            if (peaks.Count == 0)
            {
                return new List<FptEntry>();
            }

            // 구조 개선: 적응형 청크 크기 계산 (Peak 밀도 기반)
            double totalDuration = peaks[peaks.Count - 1].Time - peaks[0].Time;
            double peakDensity = peaks.Count / totalDuration; // peaks/초
            double adaptiveChunkSize = Math.Max(10.0, Math.Min(BaseChunkSizeSeconds, TargetPeaksPerChunk / peakDensity));
            int totalChunks = (int)Math.Ceiling(totalDuration / adaptiveChunkSize);
            
            //if (statusMsgCbk != null)
            //{
            //    try
            //    {
                    statusMsgCbk($"핑거프린트 생성 중... (적응형 청크: {adaptiveChunkSize:F1}초, 총 {totalChunks}개 청크, 0.0%)");
            //    }
            //    catch { }
            //}

            // 구조 개선: 순차 처리 + 즉시 타임스탬프 해제
            // 메모리 부족 문제 해결을 위해 병렬 처리를 제거하고 순차 처리로 전환
            var timestampMap = new Dictionary<int, Dictionary<string, FingerprintHashData>>(
                (int)totalDuration + 100); // 타임스탬프 개수 추정
            var completedFingerprints = new List<FptEntry>();

            // 청크 처리 진행 상황 추적
            int processedChunks = 0;
            int lastReportedChunkPercent = -1;
            DateTime startTime = DateTime.Now;
            
            // 메모리 사용량 모니터링 (청크 297/349 메모리 부족 방지: 임계값 낮춤)
            const long MemoryThresholdBytes = 300 * 1024 * 1024; // 300MB 경고 임계값 (500MB에서 감소)
            const long CriticalMemoryBytes = 500 * 1024 * 1024; // 500MB 위험 임계값 (800MB에서 감소)

            // 구조 개선: 순차 처리로 전환하여 메모리 사용량 예측 가능하게 만들기
            try
            {
                // 초기 메모리 정리 (블로킹 방지: WaitForPendingFinalizers 제거)
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                
                // 순차 처리: 각 청크를 하나씩 처리하여 메모리 사용량 제어
                for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
                {
                    try
                    {
                        // 진행 상황 초기 보고 (첫 청크 처리 시작 시)
                        if (chunkIndex == 0 && statusMsgCbk != null)
                        {
                            try
                            {
                                statusMsgCbk($"핑거프린트 생성 중... (청크 0/{totalChunks} 시작, 0%)");
                            }
                            catch { }
                        }
                        
                        // 메모리 사용량 체크 (청크 297/349 메모리 부족 방지: 매 청크마다 체크 및 강화)
                        long currentMemory = GC.GetTotalMemory(false);
                        if (currentMemory > CriticalMemoryBytes)
                        {
                            // 위험 수준: 강제 메모리 정리 및 완료된 타임스탬프 해제 (범위 확대)
                            double criticalReleaseThreshold = peaks[0].Time + (chunkIndex * adaptiveChunkSize) - (TimestampCompletionWindow * 2);
                            ReleaseCompletedTimestampsImmediate(timestampMap, completedFingerprints, criticalReleaseThreshold);
                            
                            // 메모리 부족 방지: 강제 GC 실행
                            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                            GC.WaitForPendingFinalizers();
                            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                        }
                        else if (currentMemory > MemoryThresholdBytes)
                        {
                            // 경고 수준: 완료된 타임스탬프 해제 (범위 확대)
                            if (peaks.Count > 0 && peaks[0] != null)
                            {
                                double warningReleaseThreshold = peaks[0].Time + (chunkIndex * adaptiveChunkSize) - (TimestampCompletionWindow * 1.5);
                                ReleaseCompletedTimestampsImmediate(timestampMap, completedFingerprints, warningReleaseThreshold);
                            }
                            
                            // 메모리 부족 방지: GC 실행
                            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                        }
                        
                        // 구조 개선: 적응형 청크 크기 사용
                        // null 체크: ArgumentNullException 방지
                        if (peaks.Count == 0 || peaks[0] == null)
                        {
                            processedChunks++;
                            ReportChunkProgress(processedChunks, totalChunks, chunkIndex, progress, statusMsgCbk, null, ref lastReportedChunkPercent, startTime);
                            continue;
                        }
                        
                        double chunkStartTime = peaks[0].Time + (chunkIndex * adaptiveChunkSize);
                        double chunkEndTime = chunkStartTime + adaptiveChunkSize + ChunkOverlapSeconds; // 오버랩 포함
                        
                        // 이 청크에 속하는 peak 범위 찾기 (이진 검색)
                        int chunkStartIndex = BinarySearchPeakIndex(peaks, chunkStartTime);
                        int chunkEndIndex = BinarySearchPeakIndex(peaks, chunkEndTime);
                        
                        if (chunkStartIndex >= peaks.Count || chunkEndIndex <= chunkStartIndex)
                        {
                            // 빈 청크
                            processedChunks++;
                            ReportChunkProgress(processedChunks, totalChunks, chunkIndex, progress, statusMsgCbk, null, ref lastReportedChunkPercent, startTime);
                            continue;
                        }
                        
                        // 구조 개선: Peak 리스트 복사 최적화 - 인덱스 범위만 전달하여 메모리 복사 제거
                        // 청크 단위로 핑거프린트 생성 (인덱스 범위 직접 사용)
                        // 진행 상황 보고: 청크 처리 시작 (모든 청크에서 보고)
                        if (statusMsgCbk != null)
                        {
                            try
                            {
                                statusMsgCbk($"핑거프린트 생성 중... (청크 {chunkIndex + 1}/{totalChunks} 처리 시작, Peak 범위: {chunkStartIndex}-{chunkEndIndex})");
                            }
                            catch { }
                        }
                        
                        // 청크 처리 (진행 상황 보고 포함)
                        var chunkHashTable = ProcessChunkPeaksByRange(peaks, chunkStartIndex, chunkEndIndex, sampleRate, 
                            statusMsgCbk, chunkIndex, totalChunks);

                        // 구조 개선: 즉시 타임스탬프 맵에 추가 및 완료된 타임스탬프 해제
                        ProcessChunkHashTableToTimestampMapImmediate(chunkHashTable, timestampMap, completedFingerprints, 
                            chunkEndTime - TimestampCompletionWindow);
                        
                        // 청크 297/349 메모리 부족 방지: 각 청크 처리 후 즉시 타임스탬프 해제 (범위 확대)
                        double releaseThreshold = chunkEndTime - (TimestampCompletionWindow * 1.5);
                        ReleaseCompletedTimestampsImmediate(timestampMap, completedFingerprints, releaseThreshold);
                        
                        // 청크 처리 후 즉시 메모리 정리
                        chunkHashTable.Clear();
                        chunkHashTable = null;
                        
                        // 청크 297/349 메모리 부족 방지: 매 청크마다 GC 실행 (빈도 증가)
                        // 메모리 압박 수준에 따라 GC 강도 조절
                        long memoryAfterChunk = GC.GetTotalMemory(false);
                        if (memoryAfterChunk > MemoryThresholdBytes)
                        {
                            // 메모리 압박 시 강제 GC
                            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                        }
                        else
                        {
                            // 정상 수준 시 최적화 GC
                            GC.Collect(0, GCCollectionMode.Optimized, false);
                        }
                        
                        // 청크 처리 완료 보고
                        processedChunks++;
                        ReportChunkProgress(processedChunks, totalChunks, chunkIndex, progress, statusMsgCbk, null, ref lastReportedChunkPercent, startTime);
                    }
                    catch (OutOfMemoryException)
                    {
                        // 청크 297/349 메모리 부족 방지: 완료된 타임스탬프 강제 해제 및 메모리 정리 (범위 확대)
                        try
                        {
                            // 더 넓은 범위 해제 (3배 확대)
                            double releaseThreshold = peaks[0].Time + ((chunkIndex - 1) * adaptiveChunkSize) - (TimestampCompletionWindow * 3);
                            ReleaseCompletedTimestampsImmediate(timestampMap, completedFingerprints, releaseThreshold);
                            
                            // 강제 메모리 정리
                            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                            GC.WaitForPendingFinalizers();
                            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                            GC.WaitForPendingFinalizers();
                            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                            
                            // 상태 메시지 전달
                            if (statusMsgCbk != null)
                            {
                                try
                                {
                                    statusMsgCbk($"메모리 부족으로 청크 {chunkIndex + 1} 건너뜀. 메모리 정리 완료. 계속 진행...");
                                }
                                catch { }
                            }
                        }
                        catch { }
                        
                        // 해당 청크는 건너뜀
                        processedChunks++;
                        continue;
                    }
                    catch
                    {
                        // 개별 청크 처리 실패 시 건너뜀
                        processedChunks++;
                        continue;
                    }
                }
            }
            catch
            {
                // 처리 실패 시 빈 리스트 반환
                if (processedChunks == 0)
                {
                    if (statusMsgCbk != null)
                    {
                        try
                        {
                            statusMsgCbk("청크 처리 실패");
                        }
                        catch { }
                    }
                    return new List<FptEntry>();
                }
            }

            // 구조 개선: 스트리밍 방식 - 남은 타임스탬프 처리
            TimeSpan elapsed = DateTime.Now - startTime;
            string finalizingMessage = $"청크 처리 완료 ({totalChunks}개 청크, 소요 시간: {elapsed.Minutes}분 {elapsed.Seconds}초). 최종 타임스탬프 정리 중...";
            if (statusMsgCbk != null)
            {
                try
                {
                    statusMsgCbk(finalizingMessage);
                }
                catch { }
            }
            
            if (progress != null)
            {
                try
                {
                    progress.Report(new OriginalFPProgress
                    {
                        ProcessedFrames = totalChunks,
                        TotalFrames = totalChunks,
                        ProgressPercent = 95.0,
                        CurrentTime = peaks.Count > 0 ? TimeSpan.FromSeconds(peaks[peaks.Count - 1].Time) : TimeSpan.Zero,
                        CurrentAction = finalizingMessage
                    });
                }
                catch { }
            }
            
            // 남은 타임스탬프를 FingerprintEntry로 변환 (순차 처리이므로 lock 불필요)
            var remainingTimestamps = new List<int>(timestampMap.Keys);
            remainingTimestamps.Sort();
            
            foreach (int timestamp in remainingTimestamps)
            {
                try
                {
                    if (timestampMap.TryGetValue(timestamp, out var hashDict) && hashDict.Count > 0)
                    {
                        completedFingerprints.Add(new FptEntry
                        {
                            Timestamp = timestamp,
                            Hashes = new List<FingerprintHashData>(hashDict.Values)
                        });
                    }
                }
                catch (OutOfMemoryException)
                {
                    // 메모리 부족 시 해당 타임스탬프는 건너뜀
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                    GC.WaitForPendingFinalizers();
                    break;
                }
                catch
                {
                    continue;
                }
            }
            
            timestampMap.Clear(); // 메모리 해제
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
            GC.WaitForPendingFinalizers();
            
            // 최종 정렬
            completedFingerprints.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            
            // 완료 보고
            string completeActionMessage = $"핑거프린트 생성 완료 ({completedFingerprints.Count}개 엔트리, {totalChunks}개 청크 처리)";
            if (statusMsgCbk != null)
            {
                try
                {
                    statusMsgCbk(completeActionMessage);
                }
                catch { }
            }
            
            if (progress != null)
            {
                try
                {
                    progress.Report(new OriginalFPProgress
                    {
                        ProcessedFrames = totalChunks,
                        TotalFrames = totalChunks,
                        ProgressPercent = 100.0,
                        CurrentTime = peaks.Count > 0 ? TimeSpan.FromSeconds(peaks[peaks.Count - 1].Time) : TimeSpan.Zero,
                        CurrentAction = completeActionMessage
                    });
                }
                catch { }
            }
            
            return completedFingerprints;
        }

        /// <summary>
        /// 순차 처리 방식으로 핑거프린트를 생성합니다.
        /// Thread.cs GetCurrentThreadNative() 크래시 방지를 위한 폴백 메서드입니다.
        /// </summary>
        private static List<FptEntry> GenerateFingerprintsSequential(List<Peak> peaks, int estimatedHashPairs, IProgress<OriginalFPProgress> progress = null, Action<string> statusMsgCbk = null)
        {
            if (peaks.Count == 0)
            {
                return new List<FptEntry>();
            }

            if (statusMsgCbk != null)
            {
                try
                {
                    statusMsgCbk("순차 처리 모드로 핑거프린트 생성 중...");
                }
                catch { }
            }
            
            if (progress != null)
            {
                try
                {
                    progress.Report(new OriginalFPProgress
                    {
                        ProcessedFrames = 0,
                        TotalFrames = peaks.Count,
                        ProgressPercent = 0.0,
                        CurrentTime = TimeSpan.Zero,
                        CurrentAction = "순차 처리 모드로 핑거프린트 생성 중..."
                    });
                }
                catch { }
            }

            var hashTableSequential = new Dictionary<ulong, List<FingerprintHash>>(estimatedHashPairs);
            
            for (int i = 0; i < peaks.Count; i++)
            {
                var peak1 = peaks[i];
                if (peak1 == null || double.IsNaN(peak1.Time) || double.IsInfinity(peak1.Time) ||
                    double.IsNaN(peak1.Frequency) || double.IsInfinity(peak1.Frequency))
                {
                    continue;
                }
                
                // 먼저 기본 시간 윈도우로 windowPeakCount 추정
                double windowEnd = peak1.Time + HashTimeWindow;
                int foundIndex = BinarySearchPeakIndex(peaks, windowEnd);
                int windowPeakCount = foundIndex - (i + 1);
                
                // 동적 시간 윈도우 계산
                int dynamicTimeWindow = CalculateDynamicTimeWindow(windowPeakCount);
                // 동적 시간 윈도우가 기본값과 다르면 windowEnd 재계산
                if (dynamicTimeWindow != HashTimeWindow)
                {
                    windowEnd = peak1.Time + dynamicTimeWindow;
                    foundIndex = BinarySearchPeakIndex(peaks, windowEnd);
                    windowPeakCount = foundIndex - (i + 1);
                }
                
                // Peak 밀도 기반 동적 Fan-out 계산
                int dynamicFanOut = CalculateDynamicFanOut(windowPeakCount);
                
                // ★ 위치 4: GenerateFingerprintsSequential 순차 처리 ★
                // 특이사항: 동적 시간 윈도우(CalculateDynamicTimeWindow) 적용, Dictionary로 해시 저장, Triplet 생성 포함
                var (maxInnerIterations, step, actualEndIndex) = CalculatePeakPairIterationParams(i, foundIndex, windowPeakCount, dynamicFanOut);
                int innerIterationCount = 0;

                for (int j = i + 1; j < actualEndIndex && innerIterationCount < maxInnerIterations; j++)
                {
                    innerIterationCount++;
                    
                    if (windowPeakCount > dynamicFanOut && (j - (i + 1)) % step != 0 && j != actualEndIndex - 1)
                        continue;
                    
                    var peak2 = peaks[j];
                    if (peak2 == null || double.IsNaN(peak2.Time) || double.IsInfinity(peak2.Time) ||
                        double.IsNaN(peak2.Frequency) || double.IsInfinity(peak2.Frequency) || peak2.Time > windowEnd)
                    {
                        if (peak2.Time > windowEnd) break;
                        continue;
                    }
                    
                    List<ulong> hashes;
                    try {
                        //hashes = GenerateMultipleHashes(peak1, peak2); 
                        // ★ 원본 핑거프린트 생성 (GenerateFingerprintsSequential): 항상 forIndexing=true ★
                        hashes = ImprovedHashGeneration.GenerateRobustHashes64(peak1, peak2, forIndexing: true);
                    }
                    catch { continue; }

                    double timeDelta = peak2.Time - peak1.Time;
                    // ★★★ 2026-02-07: 같은 프레임 Peak 건너뛰기 (dt=0 문제 해결) ★★★
                    if (double.IsNaN(timeDelta) || double.IsInfinity(timeDelta) || timeDelta < MinTimeDeltaForHash) continue;
                    
                    try
                    {
                        foreach (var hash in hashes)
                        {
                            if (!hashTableSequential.TryGetValue(hash, out var hashList))
                            {
                                hashList = new List<FingerprintHash>(4);
                                hashTableSequential[hash] = hashList;
                            }

                            hashList.Add(new FingerprintHash
                            {
                                Time = peak1.Time,
                                Frequency1 = peak1.Frequency,
                                Frequency2 = peak2.Frequency,
                                TimeDelta = timeDelta
                            });
                        }
                    }
                    catch { continue; }
                    
                    // Combinatorial Hashing: Triplet (3개 peak 조합) 생성
                    if (UseCombinatorialHashing && j + CombinatorialHashingStep < actualEndIndex)
                    {
                        int k = j + CombinatorialHashingStep;
                        if (k < actualEndIndex && k < peaks.Count)
                        {
                            var peak3 = peaks[k];
                            if (peak3 != null && !double.IsNaN(peak3.Time) && !double.IsInfinity(peak3.Time) &&
                                !double.IsNaN(peak3.Frequency) && !double.IsInfinity(peak3.Frequency) &&
                                peak3.Time <= windowEnd)
                            {
                                try
                                {
                                    ulong tripletHash = GeneratePeakTripletHash64(peak1, peak2, peak3);
                                    
                                    if (!hashTableSequential.TryGetValue(tripletHash, out var tripletHashList))
                                    {
                                        tripletHashList = new List<FingerprintHash>(4);
                                        hashTableSequential[tripletHash] = tripletHashList;
                                    }
                                    
                                    double timeDelta13 = peak3.Time - peak1.Time;
                                    if (!double.IsNaN(timeDelta13) && !double.IsInfinity(timeDelta13))
                                    {
                                        tripletHashList.Add(new FingerprintHash
                                        {
                                            Time = peak1.Time,
                                            Frequency1 = peak1.Frequency,
                                            Frequency2 = peak3.Frequency,
                                            TimeDelta = timeDelta13
                                        });
                                    }
                                }
                                catch { continue; }
                            }
                        }
                    }
                }
                
                // 진행 상황 보고 (1000개마다)
                if (i % 1000 == 0)
                {
                    int percent = (int)((double)i / peaks.Count * 100);
                    string actionMessage = $"핑거프린트 생성 중... (순차 처리: {i}/{peaks.Count}, {percent}%)";
                    
                    if (statusMsgCbk != null)
                    {
                        try
                        {
                            statusMsgCbk(actionMessage);
                        }
                        catch { }
                    }
                    
                    if (progress != null)
                    {
                        try
                        {
                            progress.Report(new OriginalFPProgress
                            {
                                ProcessedFrames = i,
                                TotalFrames = peaks.Count,
                                ProgressPercent = percent,
                                CurrentTime = TimeSpan.FromSeconds(peak1.Time),
                                CurrentAction = actionMessage
                            });
                        }
                        catch { }
                    }
                }
            }
            
            // 타임스탬프별 그룹화
            int estimatedTimestampsSeq = peaks.Count > 0 ? (int)Math.Ceiling(peaks[peaks.Count - 1].Time - peaks[0].Time) + 1 : 100;
            var timestampMapSeq = new Dictionary<int, Dictionary<string, FingerprintHashData>>(estimatedTimestampsSeq);

            foreach (var kvp in hashTableSequential)
            {
                string hashKey = kvp.Key.ToString("X16");
                foreach (var hash in kvp.Value)
                {
                    int timestamp = (int)Math.Floor(hash.Time);
                    if (!timestampMapSeq.TryGetValue(timestamp, out var hashSet))
                    {
                        hashSet = new Dictionary<string, FingerprintHashData>();
                        timestampMapSeq[timestamp] = hashSet;
                    }
                    if (!hashSet.ContainsKey(hashKey))
                    {
                        hashSet[hashKey] = new FingerprintHashData
                        {
                            Hash = hashKey,
                            Frequency1 = hash.Frequency1,
                            Frequency2 = hash.Frequency2,
                            TimeDelta = hash.TimeDelta
                        };
                    }
                }
            }

            var timestampsSeq = new List<int>(timestampMapSeq.Keys);
            timestampsSeq.Sort();
            var fingerprintsSeq = new List<FptEntry>(timestampsSeq.Count);
            foreach (int timestamp in timestampsSeq)
            {
                var hashDict = timestampMapSeq[timestamp];
                if (hashDict.Count > 0)
                {
                    fingerprintsSeq.Add(new FptEntry
                    {
                        Timestamp = timestamp,
                        Hashes = new List<FingerprintHashData>(hashDict.Values)
                    });
                }
            }
            
            // 완료 보고
            if (progress != null)
            {
                try
                {
                    progress.Report(new OriginalFPProgress
                    {
                        ProcessedFrames = peaks.Count,
                        TotalFrames = peaks.Count,
                        ProgressPercent = 100.0,
                        CurrentTime = peaks.Count > 0 ? TimeSpan.FromSeconds(peaks[peaks.Count - 1].Time) : TimeSpan.Zero,
                        CurrentAction = $"핑거프린트 생성 완료 ({fingerprintsSeq.Count}개 엔트리)"
                    });
                }
                catch { }
            }
            
            return fingerprintsSeq;
        }

        /// <summary>
        /// Peak 리스트에서 특정 시간에 해당하는 인덱스를 이진 검색으로 찾습니다.
        /// </summary>
        private static int BinarySearchPeakIndex(List<Peak> peaks, double targetTime)
        {
            if (peaks == null || peaks.Count == 0)
                return 0;
                
            int left = 0;
            int right = peaks.Count;
            
            while (left < right)
            {
                int mid = (left + right) / 2;
                
                // null 체크: ArgumentNullException 방지
                if (mid < 0 || mid >= peaks.Count)
                    break;
                    
                var peak = peaks[mid];
                if (peak == null)
                {
                    // null인 경우 왼쪽으로 이동 (null은 시간이 무한대라고 가정)
                    right = mid;
                    continue;
                }
                
                if (peak.Time < targetTime)
                {
                    left = mid + 1;
                }
                else
                {
                    right = mid;
                }
            }
            
            return left;
        }

        /// <summary>
        /// 구조 개선: 인덱스 범위를 직접 사용하여 Peak 리스트 복사를 제거합니다.
        /// 메모리 사용량을 대폭 감소시킵니다.
        /// </summary>
        private static Dictionary<ulong, List<FingerprintHash>> ProcessChunkPeaksByRange(
            List<Peak> allPeaks, int startIndex, int endIndex, int sampleRate, 
            Action<string> statusMsgCbk = null, int chunkIndex = -1, int totalChunks = -1)
        {
            // 진행 중단 방지: 인덱스 유효성 검사
            if (startIndex < 0 || endIndex > allPeaks.Count || startIndex >= endIndex)
            {
                return new Dictionary<ulong, List<FingerprintHash>>();
            }
            
            int chunkSize = endIndex - startIndex;
            // 청크 297/349 메모리 부족 방지: 해시 테이블 크기 제한 강화 (100000에서 50000으로 감소)
            var hashTable = new Dictionary<ulong, List<FingerprintHash>>(Math.Min(chunkSize * MaxPeaksPerWindow / 20, 50000));

            // 청크 내 peak 처리 (인덱스 범위 직접 사용)
            // 진행 중단 방지: 최대 처리 개수 제한
            int maxIterations = Math.Min(chunkSize, 100000); // 최대 10만 개 peak 처리
            int processedCount = 0;
            DateTime chunkStartTime = DateTime.Now;
            
            for (int i = startIndex; i < endIndex && processedCount < maxIterations; i++)
            {
                processedCount++;
                
                // 진행 상황 보고 (청크 14/265 멈춤 방지: 빈도 증가 - 500개 peak마다 또는 3초마다)
                //if (statusMsgCbk != null && chunkIndex >= 0 && totalChunks > 0)
                //{
                //    if (processedCount % 500 == 0 || (DateTime.Now - chunkStartTime).TotalSeconds >= 3.0)
                //    {
                //        try
                //        {
                //            int progressPercent = chunkSize > 0 ? (int)((double)processedCount / chunkSize * 100) : 0;
                //            statusMsgCbk($"핑거프린트 생성 중... (청크 {chunkIndex + 1}/{totalChunks}, Peak 처리: {processedCount}/{chunkSize} ({progressPercent}%))");
                //            chunkStartTime = DateTime.Now; // 시간 리셋
                //        }
                //        catch { }
                //    }
                //}
                
                if (i >= allPeaks.Count)
                {
                    break; // 인덱스 범위 초과 방지
                }
                
                var peak1 = allPeaks[i];
                
                // 피크 유효성 검사
                if (peak1 == null || double.IsNaN(peak1.Time) || double.IsInfinity(peak1.Time) ||
                    double.IsNaN(peak1.Frequency) || double.IsInfinity(peak1.Frequency))
                {
                    continue;
                }
                
                double windowEnd = peak1.Time + HashTimeWindow;

                // 이진 검색으로 윈도우 끝 지점 찾기 (전체 리스트에서 검색)
                int foundIndex = BinarySearchPeakIndex(allPeaks, windowEnd);
                if (foundIndex > endIndex) foundIndex = endIndex; // 청크 범위 내로 제한

                // 윈도우 내 peak 수 제한 (Peak 밀도 기반 동적 Fan-out 적용)
                int windowPeakCount = foundIndex - (i + 1);
                
                // Peak 밀도 기반 동적 Fan-out 계산
                int dynamicFanOut = CalculateDynamicFanOut(windowPeakCount);
                
                // ★ 위치 5: GenerateFingerprintsChunked 청크 처리 ★
                // 특이사항: allPeaks 사용, 청크 범위 제한(foundIndex > endIndex), 인덱스 범위 추가 검사(j >= allPeaks.Count)
                var (maxInnerIterations, step, actualEndIndex) = CalculatePeakPairIterationParams(i, foundIndex, windowPeakCount, dynamicFanOut);
                
                // 청크 14/265 멈춤 방지: actualEndIndex가 유효한지 확인 (위치 5 전용)
                if (actualEndIndex <= i + 1)
                {
                    continue; // 유효하지 않은 범위는 건너뜀
                }
                
                int innerIterationCount = 0;
                
                for (int j = i + 1; j < actualEndIndex && innerIterationCount < maxInnerIterations; j++)
                {
                    innerIterationCount++;
                    
                    // 샘플링: step 간격으로 peak 선택
                    if (windowPeakCount > dynamicFanOut && (j - (i + 1)) % step != 0 && j != actualEndIndex - 1)
                    {
                        continue;
                    }
                    
                    // 청크 14/265 멈춤 방지: 인덱스 범위 확인
                    if (j >= allPeaks.Count)
                    {
                        break; // 인덱스 범위 초과 방지
                    }
                    
                    var peak2 = allPeaks[j];

                    // 피크 유효성 검사
                    if (peak2 == null || double.IsNaN(peak2.Time) || double.IsInfinity(peak2.Time) ||
                        double.IsNaN(peak2.Frequency) || double.IsInfinity(peak2.Frequency))
                    {
                        continue;
                    }

                    if (peak2.Time > windowEnd)
                    {
                        break;
                    }

                    // 해시 생성
                    List<ulong> hashes;
                    try { 
                        //hashes = GenerateMultipleHashes(peak1, peak2); 
                        // ★ 원본 핑거프린트 생성 (GenerateFingerprintsChunked): 항상 forIndexing=true ★
                        hashes = ImprovedHashGeneration.GenerateRobustHashes64(peak1, peak2, forIndexing: true);
                    }
                    catch { continue; }

                    // 값 유효성 검사
                    double timeDelta = peak2.Time - peak1.Time;
                    // ★★★ 2026-02-07: 같은 프레임 Peak 건너뛰기 (dt=0 문제 해결) ★★★
                    if (double.IsNaN(timeDelta) || double.IsInfinity(timeDelta) || timeDelta < MinTimeDeltaForHash)
                    {
                        continue;
                    }

                    try
                    {
                        foreach (var hash in hashes)
                        {
                            if (!hashTable.TryGetValue(hash, out var hashList))
                            {
                                hashList = new List<FingerprintHash>(4);
                                hashTable[hash] = hashList;
                            }

                            hashList.Add(new FingerprintHash
                            {
                                Time = peak1.Time,
                                Frequency1 = peak1.Frequency,
                                Frequency2 = peak2.Frequency,
                                TimeDelta = timeDelta
                            });
                        }
                    }
                    catch (OutOfMemoryException)
                    {
                        // 메모리 부족 예외 처리
                        try
                        {
                            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                        }
                        catch { }
                        continue;
                    }
                    catch
                    {
                        // 기타 예외: 건너뜀
                        continue;
                    }
                    
                    // Combinatorial Hashing: Triplet (3개 peak 조합) 생성
                    if (UseCombinatorialHashing && j + CombinatorialHashingStep < actualEndIndex && j + CombinatorialHashingStep < allPeaks.Count)
                    {
                        int k = j + CombinatorialHashingStep;
                        if (k < actualEndIndex && k < allPeaks.Count)
                        {
                            var peak3 = allPeaks[k];
                            if (peak3 != null && !double.IsNaN(peak3.Time) && !double.IsInfinity(peak3.Time) &&
                                !double.IsNaN(peak3.Frequency) && !double.IsInfinity(peak3.Frequency) &&
                                peak3.Time <= windowEnd)
                            {
                                try
                                {
                                    ulong tripletHash = GeneratePeakTripletHash64(peak1, peak2, peak3);
                                    if (!hashTable.TryGetValue(tripletHash, out var tripletHashList))
                                    {
                                        tripletHashList = new List<FingerprintHash>(4);
                                        hashTable[tripletHash] = tripletHashList;
                                    }
                                    double timeDelta13 = peak3.Time - peak1.Time;
                                    if (!double.IsNaN(timeDelta13) && !double.IsInfinity(timeDelta13))
                                    {
                                        tripletHashList.Add(new FingerprintHash
                                        {
                                            Time = peak1.Time,
                                            Frequency1 = peak1.Frequency,
                                            Frequency2 = peak3.Frequency,
                                            TimeDelta = timeDelta13
                                        });
                                    }
                                }
                                catch { continue; }
                            }
                        }
                    }
                }
            }

            return hashTable;
        }

        /// <summary>
        /// 구조 개선: 순차 처리용 - 청크 해시 테이블을 타임스탬프별로 직접 그룹화합니다.
        /// lock 없이 동작하여 메모리 사용량을 최소화합니다.
        /// </summary>
        private static void ProcessChunkHashTableToTimestampMapImmediate(
            Dictionary<ulong, List<FingerprintHash>> chunkHashTable,
            Dictionary<int, Dictionary<string, FingerprintHashData>> timestampMap,
            List<FptEntry> completedFingerprints,
            double completionThreshold)
        {
            // 청크 해시 테이블을 타임스탬프별로 직접 그룹화
            foreach (var kvp in chunkHashTable)
            {
                ulong hashUint = kvp.Key;  // ★ 64비트 해시 ★
                
                string hashKey = null;
                try
                {
                    hashKey = hashUint.ToString("X16");
                }
                catch (OutOfMemoryException)
                {
                    continue; // 이 해시는 건너뜀
                }
                catch
                {
                    continue;
                }
                
                if (string.IsNullOrEmpty(hashKey))
                {
                    continue;
                }
                
                foreach (var hash in kvp.Value)
                {
                    try
                    {
                        int timestamp = (int)Math.Floor(hash.Time);
                        
                        // 타임스탬프별로 직접 추가 (순차 처리이므로 lock 불필요)
                        if (!timestampMap.TryGetValue(timestamp, out var hashDict))
                        {
                            hashDict = new Dictionary<string, FingerprintHashData>();
                            timestampMap[timestamp] = hashDict;
                        }
                        
                        if (!hashDict.ContainsKey(hashKey))
                        {
                            hashDict[hashKey] = new FingerprintHashData
                            {
                                Hash = hashKey,
                                Frequency1 = hash.Frequency1,
                                Frequency2 = hash.Frequency2,
                                TimeDelta = hash.TimeDelta
                            };
                        }
                    }
                    catch (OutOfMemoryException)
                    {
                        continue; // 이 해시 항목은 건너뜀
                    }
                    catch
                    {
                        continue;
                    }
                }
            }
        }
        
        /// <summary>
        /// 완료된 타임스탬프를 즉시 해제합니다.
        /// </summary>
        private static void ReleaseCompletedTimestampsImmediate(
            Dictionary<int, Dictionary<string, FingerprintHashData>> timestampMap,
            List<FptEntry> completedFingerprints,
            double completionThreshold)
        {
            if (completionThreshold >= double.MaxValue)
            {
                return;
            }
            
            try
            {
                var completedTimestamps = new List<int>();
                int thresholdInt = (int)Math.Floor(completionThreshold);
                
                foreach (var kvp in timestampMap)
                {
                    int timestamp = kvp.Key;
                    if (timestamp < thresholdInt)
                    {
                        completedTimestamps.Add(timestamp);
                    }
                }
                
                if (completedTimestamps.Count == 0)
                {
                    return;
                }
                
                completedTimestamps.Sort();
                int processedCount = 0;
                
                foreach (int timestamp in completedTimestamps)
                {
                    try
                    {
                        if (timestampMap.TryGetValue(timestamp, out var hashDict) && hashDict.Count > 0)
                        {
                            completedFingerprints.Add(new FptEntry
                            {
                                Timestamp = timestamp,
                                Hashes = new List<FingerprintHashData>(hashDict.Values)
                            });
                            
                            timestampMap.Remove(timestamp);
                            processedCount++;
                            
                            // 50개마다 메모리 정리
                            if (processedCount % 50 == 0)
                            {
                                GC.Collect(0, GCCollectionMode.Optimized, false);
                            }
                        }
                    }
                    catch (OutOfMemoryException)
                    {
                        break; // 더 이상 처리하지 않음
                    }
                    catch
                    {
                        continue;
                    }
                }
                
                // 완료된 타임스탬프 처리 후 메모리 정리
                if (completedTimestamps.Count > 0)
                {
                    GC.Collect(0, GCCollectionMode.Optimized, false);
                }
            }
            catch (OutOfMemoryException)
            {
                // 타임스탬프 해제 중 메모리 부족
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                GC.WaitForPendingFinalizers();
            }
            catch
            {
                // 기타 예외는 무시
            }
        }

        /// <summary>
        /// 청크 처리 진행 상황을 Thread-safe하게 보고합니다.
        /// </summary>
        private static void ReportChunkProgress(int processedChunks, int totalChunks, int currentChunkIndex,
            IProgress<OriginalFPProgress> progress, Action<string> statusMsgCbk, object progressLock, ref int lastReportedPercent, DateTime startTime)
        {
            if (statusMsgCbk == null)
            {
                return;
            }

            int currentPercent = (int)((double)processedChunks / totalChunks * 100);
            
            lock (progressLock)
            {
                if (currentPercent != lastReportedPercent)
                {
                    lastReportedPercent = currentPercent;
                    
                    // 진행 상황 표시: 청크 수에 따라 빈도 조정
                    bool shouldReport = false;
                    if (totalChunks <= 20)
                    {
                        // 청크 수가 적으면 매번 표시
                        shouldReport = true;
                    }
                    else if (totalChunks <= 100)
                    {
                        // 5개마다 표시
                        shouldReport = (processedChunks % 5 == 0) || (processedChunks == totalChunks);
                    }
                    else
                    {
                        // 1%마다 표시 (최소 1개 청크)
                        int reportInterval = Math.Max(1, totalChunks / 100);
                        shouldReport = (processedChunks % reportInterval == 0) || (processedChunks == totalChunks);
                    }
                    
                    if (shouldReport || currentPercent == 100)
                    {
                        TimeSpan elapsed = DateTime.Now - startTime;
                        string remainingTimeStr = "";
                        string estimatedEndTimeStr = "";
                        
                        if (processedChunks > 0 && processedChunks < totalChunks)
                        {
                            double estimatedTotal = elapsed.TotalSeconds / processedChunks * totalChunks;
                            double remainingSeconds = estimatedTotal - elapsed.TotalSeconds;
                            if (remainingSeconds > 0)
                            {
                                TimeSpan remaining = TimeSpan.FromSeconds(remainingSeconds);
                                
                                // 남은 시간 표시 (1시간 이상이면 시간 포함, 그 외는 분:초)
                                if (remaining.TotalHours >= 1)
                                {
                                    remainingTimeStr = $" (예상 남은 시간: {remaining.Hours}시간 {remaining.Minutes}분 {remaining.Seconds}초)";
                                }
                                else if (remaining.TotalMinutes >= 1)
                                {
                                    remainingTimeStr = $" (예상 남은 시간: {remaining.Minutes}분 {remaining.Seconds}초)";
                                }
                                else
                                {
                                    remainingTimeStr = $" (예상 남은 시간: {remaining.Seconds}초)";
                                }
                                
                                // 예상 종료 시간 계산 및 표시
                                DateTime estimatedEndTime = DateTime.Now.AddSeconds(remainingSeconds);
                                estimatedEndTimeStr = $" (예상 종료: {estimatedEndTime:HH:mm:ss})";
                            }
                        }
                        
                        try
                        {
                            // Application.Run 크래시 방지: GC.Collect 호출 제거 (UI 스레드 블로킹 방지)
                            // statusMsgCbk 호출 전후의 GC.Collect가 UI 스레드를 블로킹하여 Application.Run 크래시 발생 가능
                            
                            string message = $"핑거프린트 생성 중... (청크 {processedChunks}/{totalChunks} 완료, 현재: {currentChunkIndex + 1}번 청크 처리 중, {currentPercent}%{remainingTimeStr}{estimatedEndTimeStr})";
                            statusMsgCbk(message);
                            
                            // progress.Report()도 호출하여 CurrentAction 업데이트
                            if (progress != null)
                            {
                                try
                                {
                                    progress.Report(new OriginalFPProgress
                                    {
                                        ProcessedFrames = processedChunks,
                                        TotalFrames = totalChunks,
                                        ProgressPercent = currentPercent,
                                        CurrentTime = TimeSpan.Zero,
                                        CurrentAction = message
                                    });
                                }
                                catch { }
                            }
                        }
                        catch (OutOfMemoryException)
                        {
                            // 메모리 부족 시 statusMsgCbk 호출 건너뜀
                            try
                            {
                                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                                GC.WaitForPendingFinalizers();
                            }
                            catch { }
                        }
                        catch
                        {
                            // 기타 예외는 무시 (UI 업데이트 실패해도 처리 계속)
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 진행 상황을 Thread-safe하게 보고합니다.
        /// </summary>
        private static void ReportProgress(int processedPeakCount, int totalPeaks, int totalHashPairs, 
            Action<string> statusMsgCbk, object progressLock, ref int lastReportedPercent, DateTime startTime)
        {
            if (statusMsgCbk == null)
            {
                return;
            }

            // 진행률 계산
            int currentPercent = (int)((double)processedPeakCount / totalPeaks * 100);
            
            // 진행률이 변경되었을 때만 보고 (중복 보고 방지)
            lock (progressLock)
            {
                if (currentPercent != lastReportedPercent)
                {
                    lastReportedPercent = currentPercent;
                    
                    // 진행 상황 표시: 초기에는 자주, 이후에는 덜 자주
                    bool shouldReport = false;
                    if (processedPeakCount < 100)
                    {
                        // 처음 100개는 10개마다 표시
                        shouldReport = (processedPeakCount > 0 && processedPeakCount % 10 == 0);
                    }
                    else if (processedPeakCount < 1000)
                    {
                        // 100-1000개는 100개마다 표시
                        shouldReport = (processedPeakCount > 0 && processedPeakCount % 100 == 0);
                    }
                    else
                    {
                        // 1000개 이후는 1000개마다 또는 마지막 100개는 100개마다
                        shouldReport = (processedPeakCount > 0 && processedPeakCount % 1000 == 0) || 
                                      (processedPeakCount >= totalPeaks - 100 && processedPeakCount % 100 == 0);
                    }
                    
                    if (shouldReport || currentPercent == 100)
                    {
                        // 경과 시간 계산
                        TimeSpan elapsed = DateTime.Now - startTime;
                        
                        // 예상 남은 시간 계산 (선형 추정)
                        string remainingTimeStr = "";
                        string estimatedEndTimeStr = "";
                        if (currentPercent > 0 && currentPercent < 100)
                        {
                            double estimatedTotalSeconds = elapsed.TotalSeconds / (currentPercent / 100.0);
                            double remainingSeconds = estimatedTotalSeconds - elapsed.TotalSeconds;
                            if (remainingSeconds > 0)
                            {
                                TimeSpan remaining = TimeSpan.FromSeconds(remainingSeconds);
                                
                                // 남은 시간 표시 (1시간 이상이면 시간 포함, 그 외는 분:초)
                                if (remaining.TotalHours >= 1)
                                {
                                    remainingTimeStr = $" (예상 남은 시간: {remaining.Hours}시간 {remaining.Minutes}분 {remaining.Seconds}초)";
                                }
                                else if (remaining.TotalMinutes >= 1)
                                {
                                    remainingTimeStr = $" (예상 남은 시간: {remaining.Minutes}분 {remaining.Seconds}초)";
                                }
                                else
                                {
                                    remainingTimeStr = $" (예상 남은 시간: {remaining.Seconds}초)";
                                }
                                
                                // 예상 종료 시간 계산 및 표시
                                DateTime estimatedEndTime = DateTime.Now.AddSeconds(remainingSeconds);
                                estimatedEndTimeStr = $" (예상 종료: {estimatedEndTime:HH:mm:ss})";
                            }
                        }
                        
                        // Thread.cs GetCurrentThreadNative() 크래시 방지: statusMsgCbk 호출 전에 메모리 정리
                        // UI 스레드로 마샬링될 때 Thread.CurrentThread가 호출될 수 있으므로 메모리 확보
                        try
                        {
                            GC.Collect(0, GCCollectionMode.Optimized, false);
                            
                            // 상세한 진행 상황 표시
                            string message = $"핑거프린트 생성 중... {currentPercent:0.0}% ({processedPeakCount}/{totalPeaks} peak, 해시 쌍: {totalHashPairs:N0}개{remainingTimeStr}{estimatedEndTimeStr})";
                            statusMsgCbk(message);
                        }
                        catch (OutOfMemoryException)
                        {
                            // 메모리 부족 시 statusMsgCbk 호출 건너뜀
                        }
                        catch
                        {
                            // 기타 예외는 무시 (UI 업데이트 실패해도 처리 계속)
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 다중 해시 생성 - 약간의 변형을 허용하는 여러 해시 생성
        /// </summary>
        private static List<uint> GenerateMultipleHashes(Peak peak1, Peak peak2)
        {
            var hashes = new List<uint>();

            double timeDelta = peak2.Time - peak1.Time;
            double freqDelta = Math.Abs(peak2.Frequency - peak1.Frequency);

            // 기본 양자화
            int baseTime = (int)Math.Round(timeDelta * 5);
            int baseFreq = (int)Math.Round(freqDelta / 200);
            int f1Band = GetFrequencyBand(peak1.Frequency, lower: true);
            int f2Band = GetFrequencyBand(peak2.Frequency, lower: true);

            // 기본 해시
            hashes.Add(ComputeHash(baseTime, baseFreq, f1Band, f2Band));

            // 시간 ±1 변형 (0.2초 오차 허용)
            hashes.Add(ComputeHash(baseTime - 1, baseFreq, f1Band, f2Band));
            hashes.Add(ComputeHash(baseTime + 1, baseFreq, f1Band, f2Band));

            // 주파수 ±1 변형 (200Hz 오차 허용)
            hashes.Add(ComputeHash(baseTime, baseFreq - 1, f1Band, f2Band));
            hashes.Add(ComputeHash(baseTime, baseFreq + 1, f1Band, f2Band));

            return hashes.Distinct().ToList();
        }
        private static uint ComputeHash(int time, int freq, int band1, int band2)
        {
            const uint fnvOffsetBasis = 2166136261u;
            uint hash = fnvOffsetBasis;
            hash = FnvHash(hash, (uint)time);
            hash = FnvHash(hash, (uint)freq);
            hash = FnvHash(hash, (uint)band1);
            hash = FnvHash(hash, (uint)band2);
            return hash;
        }
        /// <summary>
        /// 주파수 대역 분류 (Mel-scale 기반)
        /// </summary>
        private static int GetFrequencyBand(double frequency, bool lower)
        {
            if (lower)
            {
                // 더 넓은 대역으로 분류 → 약간의 주파수 오차 허용
                if (frequency < 150) return 0;       // 저역 1
                if (frequency < 300) return 1;       // 저역 2
                if (frequency < 600) return 2;       // 중저역
                if (frequency < 1200) return 3;      // 중역
                if (frequency < 2400) return 4;      // 중고역
                if (frequency < 4800) return 5;      // 고역
                return 6;                            // 초고역
            } else {
                // 인간 청각에 중요한 주파수 대역으로 분류
                if (frequency < 200) return 0;       // 저역
                if (frequency < 400) return 1;
                if (frequency < 800) return 2;
                if (frequency < 1600) return 3;      // 중역
                if (frequency < 3200) return 4;
                if (frequency < 6400) return 5;      // 고역
                return 6;
            }
        }

        /// <summary>
        /// Combinatorial Hashing: 3개 피크 조합으로부터 강인한 해시를 생성합니다.
        /// </summary>
        /// <param name="peak1">첫 번째 피크</param>
        /// <param name="peak2">두 번째 피크</param>
        /// <param name="peak3">세 번째 피크</param>
        /// <returns>해시 값 (ulong) - ★★★ 2026-02-03: 32비트 → 64비트로 변경 ★★★</returns>
        private static ulong GeneratePeakTripletHash64(Peak peak1, Peak peak2, Peak peak3)
        {
            // null이면 해시 0으로 처리하여 상위 로직에서 건너뛰도록 함
            if (peak1 == null || peak2 == null || peak3 == null)
            {
                return 0UL;
            }
            
            // 시간 차이 계산
            double timeDelta12 = peak2.Time - peak1.Time;
            double timeDelta23 = peak3.Time - peak2.Time;
            
            // 주파수 차이 계산
            double freqDelta12 = Math.Abs(peak2.Frequency - peak1.Frequency);
            double freqDelta23 = Math.Abs(peak3.Frequency - peak2.Frequency);
            
            // ★★★ 2026-02-07: 해상도 향상 (옵션 A) ★★★
            // 시간: 0.1초 → 0.05초 단위 (해상도 2배)
            // 주파수: 100Hz → 50Hz 단위 (해상도 2배)
            int normalizedTime12 = (int)(timeDelta12 * 20 + 0.5);  // 0.05초 단위
            int normalizedTime23 = (int)(timeDelta23 * 20 + 0.5);  // 0.05초 단위
            int normalizedFreq12 = (int)(freqDelta12 / 50 + 0.5);  // 50Hz 단위
            int normalizedFreq23 = (int)(freqDelta23 / 50 + 0.5);  // 50Hz 단위
            int freq1 = (int)(peak1.Frequency / 50 + 0.5);         // 50Hz 단위
            int freq2 = (int)(peak2.Frequency / 50 + 0.5);         // 50Hz 단위
            int freq3 = (int)(peak3.Frequency / 50 + 0.5);         // 50Hz 단위
            
            // ★★★ FNV-1a 64비트 해시 알고리즘 사용 ★★★
            const ulong fnvOffsetBasis64 = 0xCBF29CE484222325UL;
            const ulong fnvPrime64 = 0x100000001B3UL;
            ulong hash = fnvOffsetBasis64;
            
            // ★★★ 2026-02-07: 절대 주파수 비중 증가 (옵션 C) ★★★
            // 절대 주파수를 먼저 해시에 포함 (2회 반복으로 가중치 부여)
            hash = FnvHash64(hash, fnvPrime64, freq1);
            hash = FnvHash64(hash, fnvPrime64, freq2);
            hash = FnvHash64(hash, fnvPrime64, freq3);
            hash = FnvHash64(hash, fnvPrime64, freq1);  // ★ 2회째: 가중치 부여 ★
            hash = FnvHash64(hash, fnvPrime64, freq2);  // ★ 2회째: 가중치 부여 ★
            hash = FnvHash64(hash, fnvPrime64, freq3);  // ★ 2회째: 가중치 부여 ★
            
            // 시간 차이와 주파수 차이
            hash = FnvHash64(hash, fnvPrime64, normalizedTime12);
            hash = FnvHash64(hash, fnvPrime64, normalizedTime23);
            hash = FnvHash64(hash, fnvPrime64, normalizedFreq12);
            hash = FnvHash64(hash, fnvPrime64, normalizedFreq23);
            
            return hash;
        }
        
        /// <summary>
        /// FNV-1a 64비트 해시 헬퍼
        /// </summary>
        private static ulong FnvHash64(ulong hash, ulong prime, int value)
        {
            hash ^= (ulong)(value & 0xFF);
            hash *= prime;
            hash ^= (ulong)((value >> 8) & 0xFF);
            hash *= prime;
            hash ^= (ulong)((value >> 16) & 0xFF);
            hash *= prime;
            hash ^= (ulong)((value >> 24) & 0xFF);
            hash *= prime;
            return hash;
        }
        
        /// <summary>
        /// FNV-1a 해시의 정수 값 해시
        /// </summary>
        private static uint FnvHash(uint hash, uint value)
        {
            const uint fnvPrime = 16777619u;
            // 정수를 바이트 단위로 해시 (리틀 엔디안)
            hash ^= (byte)(value & 0xFF);
            hash *= fnvPrime;
            hash ^= (byte)((value >> 8) & 0xFF);
            hash *= fnvPrime;
            hash ^= (byte)((value >> 16) & 0xFF);
            hash *= fnvPrime;
            hash ^= (byte)((value >> 24) & 0xFF);
            hash *= fnvPrime;
            return hash;
        }

        /// <summary>
        /// FFT를 수행하여 스펙트럼을 계산합니다.
        /// </summary>
        private static SpectrumResult ComputeFFT(double[] frame, int sampleRate, int fftSize)
        {
            // fftSize는 이미 2의 거듭제곱으로 정규화되어 전달됨

            // 윈도우 함수 적용 (Hamming)
            double[] windowed = new double[fftSize];
            for (int i = 0; i < frame.Length && i < fftSize; i++)
            {
                double window = frame.Length > 1
                    ? 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (frame.Length - 1))
                    : 1.0;
                windowed[i] = frame[i] * window;
            }

            double[] real = new double[fftSize];
            double[] imag = new double[fftSize];
            for (int i = 0; i < fftSize; i++)
            {
                real[i] = windowed[i];
                imag[i] = 0;
            }

            FFT(real, imag);

            int spectrumLength = fftSize / 2;
            double[] magnitudes = new double[spectrumLength];
            double[] frequencies = new double[spectrumLength];

            for (int i = 0; i < spectrumLength; i++)
            {
                double freq = i * sampleRate / (double)fftSize;
                double re = real[i];
                double im = imag[i];
                double magnitude = Math.Sqrt(re * re + im * im);
                magnitudes[i] = magnitude;
                frequencies[i] = freq;
            }

            return new SpectrumResult
            {
                Magnitudes = magnitudes,
                Frequencies = frequencies
            };
        }

        /// <summary>
        /// 2의 거듭제곱으로 올림합니다.
        /// </summary>
        private static int NextPowerOfTwo(int value)
        {
            if (value <= 0)
            {
                return 1;
            }

            int power = 1;
            while (power < value)
            {
                power <<= 1;
            }

            return power;
        }

        /// <summary>
        /// FFT (Fast Fourier Transform)를 수행합니다.
        /// </summary>
        private static void FFT(double[] real, double[] imag)
        {
            int n = real.Length;
            if (n <= 1)
            {
                return;
            }

            // Bit-reverse permutation
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                {
                    j ^= bit;
                }
                j ^= bit;

                if (i < j)
                {
                    double temp = real[i];
                    real[i] = real[j];
                    real[j] = temp;
                    temp = imag[i];
                    imag[i] = imag[j];
                    imag[j] = temp;
                }
            }

            // FFT 계산
            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = -2.0 * Math.PI / len;
                double wlenReal = Math.Cos(angle);
                double wlenImag = Math.Sin(angle);

                for (int i = 0; i < n; i += len)
                {
                    double wReal = 1;
                    double wImag = 0;

                    for (int j = 0; j < len / 2; j++)
                    {
                        double uReal = real[i + j];
                        double uImag = imag[i + j];
                        double vReal = real[i + j + len / 2] * wReal - imag[i + j + len / 2] * wImag;
                        double vImag = real[i + j + len / 2] * wReal + imag[i + j + len / 2] * wImag;

                        real[i + j] = uReal + vReal;
                        imag[i + j] = uImag + vImag;
                        real[i + j + len / 2] = uReal - vReal;
                        imag[i + j + len / 2] = uImag - vImag;

                        double nextWReal = wReal * wlenReal - wImag * wlenImag;
                        double nextWImag = wReal * wlenImag + wImag * wlenReal;
                        wReal = nextWReal;
                        wImag = nextWImag;
                    }
                }
            }
        }

        /// <summary>
        /// 핑거프린트를 파일로 저장합니다 (MessagePack 형식 사용).
        /// </summary>
        /// <param name="fingerprints">저장할 핑거프린트 리스트</param>
        /// <param name="outputFilePath">출력 파일 경로</param>
        /// <param name="context">WAV 파일 컨텍스트</param>
        /// <param name="useQuantization">데이터 양자화 사용 여부 (기본값: true)</param>
        /// <param name="hashOnly">Hash만 저장 여부 (기본값: false)</param>
        private static void Save_movieFptsToFile(List<FptEntry> fingerprints, string outputFilePath, WaveFileContext context, bool useQuantization = true, bool hashOnly = false, Action<string> statusMsgCbk = null)
        {
            // 출력 디렉토리 생성
            string directory = Path.GetDirectoryName(outputFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // MessagePack 형식만 사용 
            SaveFingerprintsToFileMessagePack(fingerprints, outputFilePath, context, hashOnly, true, statusMsgCbk);
        }

        /// <summary>
        /// 구조 개선: 중간 파일 경로를 생성합니다.
        /// </summary>
        private static string GetIntermediateFilePath(string outputFilePath, string type)
        {
            string directory = Path.GetDirectoryName(outputFilePath);
            string fileName = Path.GetFileNameWithoutExtension(outputFilePath);
            string extension = Path.GetExtension(outputFilePath);
            return Path.Combine(directory ?? "", $"{fileName}.{type}{extension}");
        }

        /// <summary>
        /// 구조 개선: Peak 리스트를 파일로 저장합니다.
        /// </summary>
        private static void SavePeaksToFile(List<Peak> peaks, string filePath, WaveFileContext context)
        {
            // 파라미터 유효성 검사
            if (peaks == null || peaks.Count == 0)
            {
                throw new ArgumentException("Peak 리스트가 null이거나 비어있습니다.");
            }
            
            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentException("파일 경로가 null이거나 비어있습니다.");
            }
            
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context), "WaveFileContext가 null입니다.");
            }

            try
            {
                // 출력 디렉토리 생성
                string directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // 구조 개선: 대용량 데이터 처리를 위한 스트리밍 방식 JSON 작성
                // JavaScriptSerializer.Serialize()는 대용량 데이터에서 메모리 부족 예외 발생 가능
                // 따라서 StreamWriter를 사용하여 직접 JSON 작성
                string tempFilePath = filePath + ".tmp";
                
                using (var writer = new StreamWriter(tempFilePath, false, Encoding.UTF8))
                {
                    // JSON 시작
                    writer.Write("{\"Version\":\"1.0\",");
                    writer.Write($"\"SampleRate\":{context.SampleRate},");
                    writer.Write($"\"Channels\":{context.Channels},");
                    writer.Write($"\"Duration\":{context.Duration.TotalSeconds},");
                    writer.Write($"\"TotalPeaks\":{peaks.Count},");
                    writer.Write("\"Peaks\":[");
                    
                    // Peak 데이터 스트리밍 작성
                    bool isFirst = true;
                    int writtenCount = 0;
                    foreach (var peak in peaks)
                    {
                        if (peak != null)
                        {
                            // NaN/Infinity 값 검사 및 처리
                            double time = double.IsNaN(peak.Time) || double.IsInfinity(peak.Time) ? 0.0 : peak.Time;
                            double frequency = double.IsNaN(peak.Frequency) || double.IsInfinity(peak.Frequency) ? 0.0 : peak.Frequency;
                            double magnitude = double.IsNaN(peak.Magnitude) || double.IsInfinity(peak.Magnitude) ? 0.0 : peak.Magnitude;
                            
                            if (!isFirst)
                            {
                                writer.Write(",");
                            }
                            
                            // JSON 직접 작성 (이스케이프 불필요 - 숫자만 사용)
                            writer.Write($"{{\"Time\":{time},");
                            writer.Write($"\"Frequency\":{frequency},");
                            writer.Write($"\"Magnitude\":{magnitude}}}");
                            
                            isFirst = false;
                            writtenCount++;
                            
                            // 10만 개마다 버퍼 플러시 (메모리 사용량 제어)
                            if (writtenCount % 100000 == 0)
                            {
                                try
                                {
                                    writer.Flush();
                                    GC.Collect(0, GCCollectionMode.Optimized, false);
                                }
                                catch
                                {
                                    // 플러시 실패는 무시하고 계속 진행
                                }
                            }
                        }
                    }
                    
                    // JSON 종료
                    writer.Write("]}");
                }
                
                // 기존 파일이 있으면 삭제 후 이동
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
                File.Move(tempFilePath, filePath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SavePeaksToFile 내부 예외: {ex.GetType().Name} - {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"파일 경로: {filePath}");
                System.Diagnostics.Debug.WriteLine($"Peak 개수: {peaks?.Count ?? 0}");
                throw; // 예외를 다시 던져서 호출자가 처리할 수 있도록
            }
        }

        /// <summary>
        /// 구조 개선: Peak 리스트를 파일에서 로드합니다.
        /// 대용량 파일 처리를 위한 개선된 JSON 파싱
        /// </summary>
        private static List<Peak> LoadPeaksFromFile(string filePath, IProgress<OriginalFPProgress> progress = null, Action<string> statusMsgCbk = null, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            try
            {
                // JavaScriptSerializer의 MaxJsonLength 설정 (대용량 JSON 지원)
                var serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue; // 최대값 설정
                
                // 파일 읽기 (메모리 부족 방지를 위해 파일 크기 확인)
                FileInfo fileInfo = new FileInfo(filePath);
                long fileSize = fileInfo.Length;
                
                // 파일이 너무 크면(500MB 이상) 스트리밍 방식으로 처리
                if (fileSize > 500 * 1024 * 1024)
                {
                    // 스트리밍 방식으로 JSON 파싱
                    return LoadPeaksFromFileStreaming(filePath, progress, statusMsgCbk, cancellationToken);
                }
                
                // 일반적인 크기면 전체 파일 읽기
                // 구조 개선: SavePeaksToFile()과 동일한 형식으로 저장되었는지 확인
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                
                // JSON 형식 검증: "Peaks":[" 패턴 확인
                if (!json.Contains("\"Peaks\":["))
                {
                    System.Diagnostics.Debug.WriteLine("LoadPeaksFromFile: JSON 형식이 올바르지 않습니다. 'Peaks' 배열을 찾을 수 없습니다.");
                    // 스트리밍 방식으로 재시도
                    return LoadPeaksFromFileStreaming(filePath, progress, statusMsgCbk, cancellationToken);
                }
                
                var data = serializer.Deserialize<Dictionary<string, object>>(json);

                if (data == null || !data.ContainsKey("Peaks"))
                {
                    System.Diagnostics.Debug.WriteLine("LoadPeaksFromFile: 'Peaks' 키를 찾을 수 없습니다.");
                    // 스트리밍 방식으로 재시도
                    return LoadPeaksFromFileStreaming(filePath, progress, statusMsgCbk, cancellationToken);
                }

                var peakListData = data["Peaks"] as List<object>;
                if (peakListData == null)
                {
                    System.Diagnostics.Debug.WriteLine("LoadPeaksFromFile: 'Peaks' 데이터가 null입니다.");
                    // 스트리밍 방식으로 재시도
                    return LoadPeaksFromFileStreaming(filePath, progress, statusMsgCbk, cancellationToken);
                }

                var peaks = new List<Peak>(peakListData.Count);
                int processedCount = 0;
                int totalCount = peakListData.Count;
                int lastReportedPercent = -1;
                
                // Duration 정보 추출 (진행 상황 표시용)
                double duration = 0.0;
                if (data.ContainsKey("Duration"))
                {
                    duration = Convert.ToDouble(data["Duration"]);
                }
                
                foreach (var item in peakListData)
                {
                    // 취소 상태 확인 (1만 개마다 또는 1%마다)
                    if (processedCount % 10000 == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    
                    try
                    {
                        Dictionary<string, object> peakData = item as Dictionary<string, object>;
                        if (peakData != null)
                        {
                            double time = Convert.ToDouble(peakData["Time"]);
                            peaks.Add(new Peak
                            {
                                Time = time,
                                Frequency = Convert.ToDouble(peakData["Frequency"]),
                                Magnitude = Convert.ToDouble(peakData["Magnitude"])
                            });
                            processedCount++;
                            
                            // 진행 상황 보고 (1%마다 또는 1만 개마다)
                            if (processedCount % 10000 == 0 || processedCount == totalCount)
                            {
                                int currentPercent = (int)((double)processedCount / totalCount * 100);
                                if (currentPercent != lastReportedPercent)
                                {
                                    lastReportedPercent = currentPercent;
                                    
                                    if (statusMsgCbk != null)
                                    {
                                        try
                                        {
                                            statusMsgCbk($"Peak 로드 중... ({processedCount}/{totalCount}, {currentPercent}%)");
                                        }
                                        catch { }
                                    }
                                    
                                    if (progress != null)
                                    {
                                        try
                                        {
                                            progress.Report(new OriginalFPProgress
                                            {
                                                ProcessedFrames = processedCount,
                                                TotalFrames = totalCount,
                                                ProgressPercent = currentPercent,
                                                CurrentTime = TimeSpan.FromSeconds(time),
                                                CurrentAction = $"Peak 로드 중... ({processedCount}/{totalCount}, {currentPercent}%)"
                                            });
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // 개별 Peak 파싱 실패는 건너뜀
                        System.Diagnostics.Debug.WriteLine($"Peak 파싱 실패: {ex.Message}");
                    }
                }

                System.Diagnostics.Debug.WriteLine($"LoadPeaksFromFile: {processedCount}/{peakListData.Count}개 Peak 로드 완료");
                return peaks.Count > 0 ? peaks : null;
            }
            catch (OperationCanceledException)
            {
                // 취소 요청 시 즉시 중단
                System.Diagnostics.Debug.WriteLine("LoadPeaksFromFile: 취소 요청됨");
                return null;
            }
            catch (OutOfMemoryException ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadPeaksFromFile OutOfMemoryException: {ex.Message}");
                // 메모리 부족 시 스트리밍 방식으로 재시도
                try
                {
                    return LoadPeaksFromFileStreaming(filePath, progress, statusMsgCbk, cancellationToken);
                }
                catch
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadPeaksFromFile 예외: {ex.GetType().Name} - {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"파일 경로: {filePath}");
                return null;
            }
        }

        /// <summary>
        /// 스트리밍 방식으로 Peak 파일을 로드합니다 (대용량 파일용)
        /// 구조 개선: SavePeaksToFile()과 동일한 형식으로 저장된 파일을 안전하게 로드
        /// </summary>
        private static List<Peak> LoadPeaksFromFileStreaming(string filePath, IProgress<OriginalFPProgress> progress = null, Action<string> statusMsgCbk = null, CancellationToken cancellationToken = default)
        {
            var peaksList = new List<Peak>();
            
            try
            {
                // 구조 개선: SavePeaksToFile()이 저장한 형식과 동일하게 파싱
                // 저장 형식: {"Version":"1.0","SampleRate":...,"Channels":...,"Duration":...,"TotalPeaks":...,"Peaks":[{...},{...},...]}
                using (var reader = new StreamReader(filePath, Encoding.UTF8, true, 8192)) // 8KB 버퍼
                {
                    // "Peaks":[" 위치까지 건너뛰기 (스트리밍으로 찾기)
                    bool foundPeaksArray = false;
                    var searchBuffer = new StringBuilder(256);
                    char[] searchCharBuffer = new char[1];
                    int searchCount = 0;
                    const int maxSearchChars = 10000; // 최대 10KB까지 헤더 검색
                    
                    while (!foundPeaksArray && searchCount < maxSearchChars)
                    {
                        // 취소 상태 확인 (매 1000바이트마다)
                        if (searchCount % 1000 == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        
                        int read = reader.Read(searchCharBuffer, 0, 1);
                        if (read <= 0)
                        {
                            break;
                        }
                        
                        searchCount++;
                        searchBuffer.Append(searchCharBuffer[0]);
                        string current = searchBuffer.ToString();
                        
                        // 버퍼가 너무 커지면 최근 부분만 유지 (메모리 사용량 제어)
                        if (searchBuffer.Length > 200)
                        {
                            searchBuffer.Remove(0, searchBuffer.Length - 100);
                            current = searchBuffer.ToString();
                        }
                        
                        // "Peaks":[" 패턴 찾기
                        if (current.Contains("\"Peaks\":["))
                        {
                            int idx = current.LastIndexOf("\"Peaks\":[");
                            if (idx >= 0)
                            {
                                int bracketIdx = current.IndexOf('[', idx);
                                if (bracketIdx >= 0)
                                {
                                    // '[' 다음 위치로 이동 완료
                                    foundPeaksArray = true;
                                    break;
                                }
                            }
                        }
                    }
                    
                    if (!foundPeaksArray)
                    {
                        System.Diagnostics.Debug.WriteLine("LoadPeaksFromFileStreaming: 'Peaks' 배열을 찾을 수 없습니다.");
                        return null;
                    }
                    
                    // TotalPeaks 정보 추출 (진행 상황 표시용)
                    int totalPeaks = 0;
                    string headerText = searchBuffer.ToString();
                    if (headerText.Contains("\"TotalPeaks\""))
                    {
                        try
                        {
                            int totalPeaksIdx = headerText.IndexOf("\"TotalPeaks\"");
                            if (totalPeaksIdx >= 0)
                            {
                                int colonIdx = headerText.IndexOf(':', totalPeaksIdx);
                                if (colonIdx >= 0)
                                {
                                    int commaIdx = headerText.IndexOf(',', colonIdx);
                                    if (commaIdx > colonIdx)
                                    {
                                        string totalPeaksStr = headerText.Substring(colonIdx + 1, commaIdx - colonIdx - 1).Trim();
                                        if (int.TryParse(totalPeaksStr, out totalPeaks))
                                        {
                                            if (statusMsgCbk != null)
                                            {
                                                try
                                                {
                                                    statusMsgCbk($"Peak 파일 스트리밍 로드 시작 (총 {totalPeaks}개 예상)");
                                                }
                                                catch { }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    
                    // JSON 객체를 스트리밍 방식으로 파싱
                    var serializer = new JavaScriptSerializer();
                    int processedCount = 0;
                    int lastReportedPercent = -1;
                    var currentObjBuilder = new StringBuilder(256); // 개별 객체용 작은 버퍼
                    bool inObject = false;
                    int depth = 0;
                    char[] readBuffer = new char[8192];
                    double lastReportedTime = 0.0;
                    
                    while (true)
                    {
                        // 취소 상태 확인 (매 버퍼 읽기 전)
                        cancellationToken.ThrowIfCancellationRequested();
                        
                        int bytesRead = reader.Read(readBuffer, 0, readBuffer.Length);
                        if (bytesRead <= 0)
                        {
                            break;
                        }
                        
                        for (int i = 0; i < bytesRead; i++)
                        {
                            char c = readBuffer[i];
                            
                            if (c == '{')
                            {
                                if (!inObject)
                                {
                                    inObject = true;
                                    depth = 1;
                                    currentObjBuilder.Clear();
                                    currentObjBuilder.Append(c);
                                }
                                else
                                {
                                    depth++;
                                    currentObjBuilder.Append(c);
                                }
                            }
                            else if (c == '}')
                            {
                                if (inObject)
                                {
                                    currentObjBuilder.Append(c);
                                    depth--;
                                    
                                    if (depth == 0)
                                    {
                                        // 객체 완성 - 파싱 시도
                                        try
                                        {
                                            string objStr = currentObjBuilder.ToString();
                                            var peakData = serializer.Deserialize<Dictionary<string, object>>(objStr);
                                            if (peakData != null && peakData.ContainsKey("Time") && peakData.ContainsKey("Frequency") && peakData.ContainsKey("Magnitude"))
                                            {
                                                double time = Convert.ToDouble(peakData["Time"]);
                                                peaksList.Add(new Peak
                                                {
                                                    Time = time,
                                                    Frequency = Convert.ToDouble(peakData["Frequency"]),
                                                    Magnitude = Convert.ToDouble(peakData["Magnitude"])
                                                });
                                                processedCount++;
                                                lastReportedTime = time;
                                                
                                                // 진행 상황 보고 및 취소 확인 (1만 개마다 또는 1%마다)
                                                if (totalPeaks > 0 && (processedCount % 10000 == 0 || processedCount == totalPeaks))
                                                {
                                                    // 취소 상태 확인
                                                    cancellationToken.ThrowIfCancellationRequested();
                                                    
                                                    int currentPercent = (int)((double)processedCount / totalPeaks * 100);
                                                    if (currentPercent != lastReportedPercent)
                                                    {
                                                        lastReportedPercent = currentPercent;
                                                        
                                                        if (statusMsgCbk != null)
                                                        {
                                                            try
                                                            {
                                                                statusMsgCbk($"Peak 스트리밍 로드 중... ({processedCount}/{totalPeaks}, {currentPercent}%)");
                                                            }
                                                            catch { }
                                                        }
                                                        
                                                        if (progress != null)
                                                        {
                                                            try
                                                            {
                                                                progress.Report(new OriginalFPProgress
                                                                {
                                                                    ProcessedFrames = processedCount,
                                                                    TotalFrames = totalPeaks,
                                                                    ProgressPercent = currentPercent,
                                                                    CurrentTime = TimeSpan.FromSeconds(time),
                                                                    CurrentAction = $"Peak 스트리밍 로드 중... ({processedCount}/{totalPeaks}, {currentPercent}%)"
                                                                });
                                                            }
                                                            catch { }
                                                        }
                                                    }
                                                }
                                                else if (totalPeaks == 0 && processedCount % 10000 == 0)
                                                {
                                                    // 취소 상태 확인
                                                    cancellationToken.ThrowIfCancellationRequested();
                                                    
                                                    // TotalPeaks 정보가 없을 때는 개수만 표시
                                                    if (statusMsgCbk != null)
                                                    {
                                                        try
                                                        {
                                                            statusMsgCbk($"Peak 스트리밍 로드 중... ({processedCount}개)");
                                                        }
                                                        catch { }
                                                    }
                                                    
                                                    if (progress != null)
                                                    {
                                                        try
                                                        {
                                                            progress.Report(new OriginalFPProgress
                                                            {
                                                                ProcessedFrames = processedCount,
                                                                TotalFrames = processedCount,
                                                                ProgressPercent = 50.0, // 알 수 없으므로 50%로 설정
                                                                CurrentTime = TimeSpan.FromSeconds(time),
                                                                CurrentAction = $"Peak 스트리밍 로드 중... ({processedCount}개)"
                                                            });
                                                        }
                                                        catch { }
                                                    }
                                                }
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            // 개별 객체 파싱 실패는 건너뜀
                                            System.Diagnostics.Debug.WriteLine($"Peak 객체 파싱 실패: {ex.Message}");
                                        }
                                        
                                        inObject = false;
                                        currentObjBuilder.Clear();
                                        
                                        // 10만 개마다 메모리 정리
                                        if (peaksList.Count % 100000 == 0)
                                        {
                                            GC.Collect(0, GCCollectionMode.Optimized, false);
                                        }
                                    }
                                }
                            }
                            else if (inObject)
                            {
                                currentObjBuilder.Append(c);
                            }
                            else if (c == ']')
                            {
                                // 배열 종료
                                break;
                            }
                        }
                        
                        // 배열 종료 확인
                        if (!inObject && bytesRead < readBuffer.Length)
                        {
                            break;
                        }
                    }
                    
                    // 최종 진행 상황 보고
                    if (progress != null && processedCount > 0)
                    {
                        try
                        {
                            progress.Report(new OriginalFPProgress
                            {
                                ProcessedFrames = processedCount,
                                TotalFrames = totalPeaks > 0 ? totalPeaks : processedCount,
                                ProgressPercent = 100.0,
                                CurrentTime = TimeSpan.FromSeconds(lastReportedTime),
                                CurrentAction = $"Peak 로드 완료 ({processedCount}개)"
                            });
                        }
                        catch { }
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"LoadPeaksFromFileStreaming: {processedCount}개 Peak 로드 완료");
                }
                
                return peaksList.Count > 0 ? peaksList : null;
            }
            catch (OperationCanceledException)
            {
                // 취소 요청 시 즉시 중단
                System.Diagnostics.Debug.WriteLine("LoadPeaksFromFileStreaming: 취소 요청됨");
                peaksList?.Clear();
                return null;
            }
            catch (OutOfMemoryException ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadPeaksFromFileStreaming OutOfMemoryException: {ex.Message}");
                // 메모리 정리 시도
                try
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                    GC.WaitForPendingFinalizers();
                }
                catch { }
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadPeaksFromFileStreaming 예외: {ex.GetType().Name} - {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"파일 경로: {filePath}");
                return null;
            }
        }

        /// <summary>
        /// 핑거프린트 리스트를 파일에서 로드합니다 (기존 방식 - 호환성)
        /// </summary>
        public static List<FptEntry> LoadFingerprintsFromFile(string filePath)
        {
            return LoadFingerprintsFromFile(filePath, out _);
        }

        /// <summary>
        /// 핑거프린트 리스트와 역인덱스를 파일에서 로드합니다 (역인덱스 포함)
        /// </summary>
        /// <param name="filePath">파일 경로</param>
        /// <param name="hashToTimestamps">역인덱스 (출력, 선택적)</param>
        /// <returns>핑거프린트 엔트리 리스트</returns>
        public static List<FptEntry> LoadFingerprintsFromFile(string filePath, out Dictionary<ulong, List<int>> hashToTimestamps)
        {
            hashToTimestamps = null;

            if (!File.Exists(filePath))
            {
                System.Diagnostics.Debug.WriteLine($"LoadFingerprintsFromFile: 파일이 존재하지 않습니다: {filePath}");
                return null;
            }

            try
            {
                // MessagePack 형식만 사용 (JSON/Gzip 제거됨)
                var serializer = AudioViewStudio.Analysis.MessagePackSerializerFactory.Create();
                
                // CanLoad 확인 (디버깅 정보 출력)
                bool canLoad = serializer.CanLoad(filePath);
                System.Diagnostics.Debug.WriteLine($"LoadFingerprintsFromFile: CanLoad 결과: {canLoad}");
                
                if (canLoad)
                {
                    System.Diagnostics.Debug.WriteLine($"LoadFingerprintsFromFile: MessagePack 파일 로드 시도: {filePath}");
                    
                    // 역인덱스를 사용할 수 있는 구현체인지 확인
                    if (serializer is AudioViewStudio.Analysis.MessagePackFingerprintSerializer mpSerializer)
                    {
                        // 역인덱스를 포함한 로드 (오버로드 사용)
                        var result = mpSerializer.Load(filePath, out var context, out hashToTimestamps);
                        if (result != null && result.Count > 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"LoadFingerprintsFromFile: MessagePack 파일 로드 성공 ({result.Count}개 엔트리, 역인덱스: {(hashToTimestamps != null ? hashToTimestamps.Count.ToString() : "없음")}개 해시)");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"LoadFingerprintsFromFile: MessagePack 파일 로드 결과가 null이거나 비어있음");
                        }
                        return result;
                    }
                    else
                    {
                        // 일반 로드 (역인덱스 없음)
                        var result = serializer.Load(filePath, out var context);
                        hashToTimestamps = null;
                        if (result != null && result.Count > 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"LoadFingerprintsFromFile: MessagePack 파일 로드 성공 ({result.Count}개 엔트리, 역인덱스: 없음)");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"LoadFingerprintsFromFile: MessagePack 파일 로드 결과가 null이거나 비어있음");
                        }
                        return result;
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"LoadFingerprintsFromFile: MessagePack Serializer가 파일을 로드할 수 없음: {filePath}");
                    hashToTimestamps = null;
                    return null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadFingerprintsFromFile: 예외 발생: {ex.GetType().Name} - {ex.Message}");
                if (ex.InnerException != null)
                {
                    System.Diagnostics.Debug.WriteLine($"  내부 예외: {ex.InnerException.GetType().Name} - {ex.InnerException.Message}");
                }
                System.Diagnostics.Debug.WriteLine($"  스택 트레이스: {ex.StackTrace}");
                hashToTimestamps = null;
                return null;
            }
        }

        /// <summary>
        /// 원본 방식과 라이브 방식의 해시 생성을 직접 비교하는 진단 함수
        /// 동일한 오디오에서 동일한 위치의 해시가 일치하는지 확인합니다.
        /// </summary>
        public static void DiagnoseHashGenerationDifference(
            string audioFilePath, 
            TimeSpan startTime, 
            int durationMs,
            Action<string> statusCallback = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("\n=== 해시 생성 비교 진단 ===");
            
            try
            {
                // 1. 원본 방식으로 샘플 읽기
                int sampleRate;
                float[] samples = ReadAudioSamplesForLive(audioFilePath, startTime, durationMs, out sampleRate);
                
                if (samples == null || samples.Length == 0)
                {
                    sb.AppendLine("❌ 샘플 읽기 실패");
                    statusCallback?.Invoke(sb.ToString());
                    return;
                }
                
                sb.AppendLine($"[오디오 정보]");
                sb.AppendLine($"  파일: {Path.GetFileName(audioFilePath)}");
                sb.AppendLine($"  시작: {startTime:hh\\:mm\\:ss\\.fff}");
                sb.AppendLine($"  길이: {durationMs}ms");
                sb.AppendLine($"  샘플 레이트: {sampleRate}Hz");
                sb.AppendLine($"  샘플 수: {samples.Length}");
                
                // 2. double 배열로 변환
                double[] doubleSamples = new double[samples.Length];
                for (int i = 0; i < samples.Length; i++)
                {
                    doubleSamples[i] = samples[i];
                }
                
                // 3. 기본 FingerprintConfig 생성
                var param = new PickAudioFpParam
                {
                    sampleRate = sampleRate,
                    fptCfg = new FingerprintConfig
                    {
                        FFTSize = 4096,
                        HopSize = 2048,
                        MaxPeaksPerFrame = 5,
                        PeakNeighborhoodSize = 5,
                        PeakThresholdMultiplier = 2.0
                    }
                };
                
                // 4. Peak 추출 (라이브 방식)
                var peaks = ExtractPeaksFromSamples(doubleSamples, param);
                
                sb.AppendLine($"\n[Peak 추출 결과]");
                sb.AppendLine($"  총 Peak 수: {peaks.Count}");
                
                if (peaks.Count > 0)
                {
                    // 시간순 정렬
                    // ★ 시간 → 주파수 → Magnitude 순으로 3차 정렬 (완전 결정적 정렬) ★
                    peaks.Sort((p1, p2) =>
                    {
                        int cmp = p1.Time.CompareTo(p2.Time);
                        if (cmp != 0) return cmp;
                        cmp = p1.Frequency.CompareTo(p2.Frequency);
                        if (cmp != 0) return cmp;
                        return p1.Magnitude.CompareTo(p2.Magnitude);
                    });
                    
                    sb.AppendLine($"  첫 번째 Peak: Time={peaks[0].Time:F4}s, Freq={peaks[0].Frequency:F1}Hz");
                    sb.AppendLine($"  마지막 Peak: Time={peaks[peaks.Count-1].Time:F4}s, Freq={peaks[peaks.Count-1].Frequency:F1}Hz");
                    
                    // 처음 5개 Peak 상세
                    sb.AppendLine($"\n[처음 5개 Peak 상세]");
                    for (int i = 0; i < Math.Min(5, peaks.Count); i++)
                    {
                        sb.AppendLine($"  Peak[{i}]: Time={peaks[i].Time:F4}s, Freq={peaks[i].Frequency:F1}Hz, Mag={peaks[i].Magnitude:E2}");
                    }
                }
                
                // 5. 핑거프린트 생성
                var fingerprints = GenerateFingerprints(peaks, sampleRate);
                
                sb.AppendLine($"\n[핑거프린트 생성 결과]");
                sb.AppendLine($"  총 엔트리 수: {fingerprints.Count}");
                int totalHashes = fingerprints.Sum(f => f.Hashes?.Count ?? 0);
                sb.AppendLine($"  총 해시 수: {totalHashes}");
                
                if (fingerprints.Count > 0 && fingerprints[0].Hashes != null && fingerprints[0].Hashes.Count > 0)
                {
                    sb.AppendLine($"\n[처음 10개 해시]");
                    int hashCount = 0;
                    foreach (var entry in fingerprints)
                    {
                        if (entry.Hashes == null) continue;
                        foreach (var hash in entry.Hashes)
                        {
                            if (hashCount >= 10) break;
                            sb.AppendLine($"  [{entry.Timestamp}s] {hash.Hash} (F1={hash.Frequency1:F0}, F2={hash.Frequency2:F0}, dt={hash.TimeDelta:F3})");
                            hashCount++;
                        }
                        if (hashCount >= 10) break;
                    }
                }
                
            // 6. FingerprintConfig 정보 출력
            sb.AppendLine($"\n[FingerprintConfig]");
            sb.AppendLine($"  FFTSize: {param.fptCfg.FFTSize}");
            sb.AppendLine($"  HopSize: {param.fptCfg.HopSize}");
            sb.AppendLine($"  MaxPeaksPerFrame: {param.fptCfg.MaxPeaksPerFrame}");
            sb.AppendLine($"  PeakThresholdMultiplier: {param.fptCfg.PeakThresholdMultiplier}");
            
            // ★ 실제 오디오 위치 정보 출력 ★
            int actualTimestamp = (int)startTime.TotalSeconds;
            sb.AppendLine($"\n[★ 중요: 실제 오디오 위치 ★]");
            sb.AppendLine($"  추출 시작 시간: {startTime} = {actualTimestamp}초");
            sb.AppendLine($"  → 역인덱스에서 {actualTimestamp}초 구간의 해시와 비교해야 함!");
            
            // ★ 원본 방식으로 Peak 추출하여 비교 ★
            sb.AppendLine($"\n[★ 원본 방식 Peak 추출 테스트 ★]");
            try
            {
                // 원본 방식: ProcessFrame과 동일한 로직으로 Peak 추출
                var origPeaksBag = new ConcurrentBag<Peak>();
                int origFftSize = param.fptCfg.FFTSize;
                int origHopSize = param.fptCfg.HopSize;
                
                // Hamming 윈도우
                double[] origHammingWindow = new double[origFftSize];
                for (int i = 0; i < origFftSize; i++)
                {
                    origHammingWindow[i] = origFftSize > 1 ? 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (origFftSize - 1)) : 1.0;
                }
                
                int origSpectrumLength = origFftSize / 2;
                double[] origFrequencies = new double[origSpectrumLength];
                for (int i = 0; i < origSpectrumLength; i++)
                {
                    origFrequencies[i] = i * sampleRate / (double)origFftSize;
                }
                
                // 첫 번째 프레임만 처리 (원본 방식)
                double[] origFrame = new double[origFftSize];
                double[] origReal = new double[origFftSize];
                double[] origImag = new double[origFftSize];
                double[] origMagnitudes = new double[origSpectrumLength];
                
                Array.Copy(doubleSamples, 0, origFrame, 0, Math.Min(origFftSize, doubleSamples.Length));
                
                for (int i = 0; i < origFftSize; i++)
                {
                    origReal[i] = origFrame[i] * origHammingWindow[i];
                    origImag[i] = 0;
                }
                
                FFT(origReal, origImag);
                
                for (int i = 0; i < origSpectrumLength; i++)
                {
                    origMagnitudes[i] = origReal[i] * origReal[i] + origImag[i] * origImag[i];
                }
                
                // Peak 검출 (원본 방식)
                ImprovedPeakDetection.DetectPeaksAdaptive(origPeaksBag, origMagnitudes, origFrequencies, 0.0, param.fptCfg);
                var origPeaksList = origPeaksBag.ToList().OrderBy(p => p.Frequency).ToList();
                
                sb.AppendLine($"  원본 방식 Peak 수: {origPeaksList.Count}");
                for (int i = 0; i < Math.Min(5, origPeaksList.Count); i++)
                {
                    sb.AppendLine($"    Peak[{i}]: Freq={origPeaksList[i].Frequency:F1}Hz, Mag={origPeaksList[i].Magnitude:E2}");
                }
                
                // 라이브 방식과 비교
                var livePeaksSorted = peaks.Where(p => Math.Abs(p.Time) < 0.001).OrderBy(p => p.Frequency).ToList();
                sb.AppendLine($"  라이브 방식 Peak 수 (Time=0): {livePeaksSorted.Count}");
                for (int i = 0; i < Math.Min(5, livePeaksSorted.Count); i++)
                {
                    sb.AppendLine($"    Peak[{i}]: Freq={livePeaksSorted[i].Frequency:F1}Hz, Mag={livePeaksSorted[i].Magnitude:E2}");
                }
                
                // 일치 여부 확인
                bool peaksMatch = origPeaksList.Count == livePeaksSorted.Count;
                if (peaksMatch)
                {
                    for (int i = 0; i < origPeaksList.Count; i++)
                    {
                        if (Math.Abs(origPeaksList[i].Frequency - livePeaksSorted[i].Frequency) > 1.0)
                        {
                            peaksMatch = false;
                            break;
                        }
                    }
                }
                sb.AppendLine($"  Peak 일치 여부: {(peaksMatch ? "✓ 일치" : "✗ 불일치")}");
            }
            catch (Exception ex3)
            {
                sb.AppendLine($"  ❌ 원본 방식 Peak 추출 실패: {ex3.Message}");
            }
            
            // 7. 원본 방식과 비교 - 동일한 위치에서 Peak/해시 생성
            sb.AppendLine($"\n[원본 방식으로 Peak 추출 비교]");
            try
            {
                // 원본과 동일한 방식으로 Peak 추출 (ProcessFrame 방식 시뮬레이션)
                var context = ParseWaveHeader(audioFilePath);
                int origSampleRate = context.SampleRate;
                int origChannels = context.Channels;
                int origBytesPerSample = context.BitsPerSample / 8;
                int origFftSize = param.fptCfg.FFTSize;
                int origHopSize = param.fptCfg.HopSize;
                
                // 시작 위치 계산
                long startSampleMono = (long)(startTime.TotalSeconds * origSampleRate);
                long fileStartPosition = context.DataStartPosition + (startSampleMono * origChannels * origBytesPerSample);
                
                sb.AppendLine($"  원본 샘플 레이트: {origSampleRate}Hz");
                sb.AppendLine($"  시작 모노 샘플: {startSampleMono}");
                sb.AppendLine($"  파일 시작 위치: {fileStartPosition}");
                
                // 첫 번째 프레임 읽기 (원본 방식)
                using (var fs = File.Open(audioFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false))
                {
                    reader.BaseStream.Seek(fileStartPosition, SeekOrigin.Begin);
                    
                    // 첫 번째 프레임 샘플 읽기
                    int samplesToRead = origFftSize * origChannels;
                    var frameSamples = new List<double>();
                    
                    for (int i = 0; i < samplesToRead; i++)
                    {
                        if (reader.BaseStream.Position >= reader.BaseStream.Length) break;
                        
                        double sample = 0;
                        switch (context.BitsPerSample)
                        {
                            case 16:
                                sample = reader.ReadInt16() / 32768.0;
                                break;
                            case 24:
                                byte[] bytes24 = reader.ReadBytes(3);
                                if (bytes24.Length < 3) break;
                                int sample24 = (bytes24[0] | (bytes24[1] << 8) | (bytes24[2] << 16));
                                if ((sample24 & 0x800000) != 0) sample24 |= unchecked((int)0xFF000000);
                                sample = sample24 / 8388608.0;
                                break;
                            case 32:
                                sample = reader.ReadSingle();
                                break;
                        }
                        frameSamples.Add(sample);
                    }
                    
                    // 모노로 변환
                    double[] monoFrame = new double[origFftSize];
                    if (origChannels == 2)
                    {
                        for (int i = 0; i < origFftSize && i * 2 + 1 < frameSamples.Count; i++)
                        {
                            monoFrame[i] = (frameSamples[i * 2] + frameSamples[i * 2 + 1]) / 2.0;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < origFftSize && i < frameSamples.Count; i++)
                        {
                            monoFrame[i] = frameSamples[i];
                        }
                    }
                    
                    // 처음 5개 샘플 출력
                    sb.AppendLine($"  원본 방식 첫 5개 샘플:");
                    for (int i = 0; i < Math.Min(5, monoFrame.Length); i++)
                    {
                        sb.AppendLine($"    [{i}] = {monoFrame[i]:E6}");
                    }
                }
                
                // 라이브 방식 첫 5개 샘플 출력
                sb.AppendLine($"  라이브 방식 첫 5개 샘플:");
                for (int i = 0; i < Math.Min(5, doubleSamples.Length); i++)
                {
                    sb.AppendLine($"    [{i}] = {doubleSamples[i]:E6}");
                }
                
                sb.AppendLine($"  ★ 위의 '원본 방식'과 '라이브 방식' 샘플 값을 비교하세요 ★");
                sb.AppendLine($"  (값이 다르면 오디오 읽기 방식에 차이가 있음)");
            }
            catch (Exception ex2)
            {
                sb.AppendLine($"  ❌ 원본 방식 비교 실패: {ex2.Message}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"❌ 예외 발생: {ex.Message}");
        }
        
        statusCallback?.Invoke(sb.ToString());
    }

        /// <summary>
        /// WAV 파일에서 지정된 시간 구간의 샘플을 읽습니다.
        /// 원본 핑거프린트 생성과 동일한 방식으로 읽어 일관성을 보장합니다.
        /// </summary>
        /// <param name="audioFilePath">WAV 파일 경로</param>
        /// <param name="startTime">시작 시간</param>
        /// <param name="durationMs">읽을 구간 (밀리초)</param>
        /// <param name="sampleRate">출력: 샘플 레이트</param>
        /// <returns>모노 샘플 배열 (float)</returns>
        public static float[] ReadAudioSamplesForLive(string audioFilePath, TimeSpan startTime, int durationMs, out int sampleRate)
        {
            sampleRate = 0;
            
            try
            {
                var context = ParseWaveHeader(audioFilePath);
                sampleRate = context.SampleRate;
                int channels = context.Channels;
                int bytesPerSample = context.BitsPerSample / 8;
                
                // 시작 위치 계산 (모노 샘플 기준)
                long startSampleMono = (long)(startTime.TotalSeconds * sampleRate);
                long totalMonoSamples = channels == 2 ? context.TotalSamples / 2 : context.TotalSamples;
                
                if (startSampleMono >= totalMonoSamples)
                {
                    return Array.Empty<float>();
                }
                
                // 읽을 샘플 수 계산
                int samplesToRead = (int)(sampleRate * durationMs / 1000.0);
                if (startSampleMono + samplesToRead > totalMonoSamples)
                {
                    samplesToRead = (int)(totalMonoSamples - startSampleMono);
                }
                
                if (samplesToRead <= 0)
                {
                    return Array.Empty<float>();
                }
                
                // 파일에서 읽기
                using (var fs = File.Open(audioFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false))
                {
                    // 데이터 시작 위치로 이동
                    long fileStartPosition = context.DataStartPosition + (startSampleMono * channels * bytesPerSample);
                    reader.BaseStream.Seek(fileStartPosition, SeekOrigin.Begin);
                    
                    // 샘플 읽기 (스테레오인 경우 양 채널 모두 읽기)
                    int totalSamplesToRead = samplesToRead * channels;
                    var samples = new List<double>(totalSamplesToRead);
                    
                    for (int i = 0; i < totalSamplesToRead; i++)
                    {
                        long remainingBytes = reader.BaseStream.Length - reader.BaseStream.Position;
                        if (remainingBytes < bytesPerSample)
                        {
                            break;
                        }
                        
                        double sample = 0;
                        switch (context.BitsPerSample)
                        {
                            case 8:
                                sample = (reader.ReadByte() - 128) / 128.0;
                                break;
                            case 16:
                                sample = reader.ReadInt16() / 32768.0;
                                break;
                            case 24:
                                byte[] bytes24 = reader.ReadBytes(3);
                                if (bytes24.Length < 3) break;
                                int sample24 = (bytes24[0] | (bytes24[1] << 8) | (bytes24[2] << 16));
                                if ((sample24 & 0x800000) != 0)
                                {
                                    sample24 |= unchecked((int)0xFF000000);
                                }
                                sample = sample24 / 8388608.0;
                                break;
                            case 32:
                                if (context.AudioFormat == 1) // PCM
                                {
                                    sample = reader.ReadInt32() / 2147483648.0;
                                }
                                else // IEEE float
                                {
                                    sample = reader.ReadSingle();
                                }
                                break;
                        }
                        samples.Add(sample);
                    }
                    
                    // 스테레오인 경우 모노로 변환 (평균)
                    float[] result;
                    if (channels == 2)
                    {
                        result = new float[samples.Count / 2];
                        for (int i = 0; i < result.Length; i++)
                        {
                            if (i * 2 + 1 < samples.Count)
                            {
                                result[i] = (float)((samples[i * 2] + samples[i * 2 + 1]) / 2.0);
                            }
                            else
                            {
                                result[i] = (float)samples[i * 2];
                            }
                        }
                    }
                    else
                    {
                        result = new float[samples.Count];
                        for (int i = 0; i < result.Length; i++)
                        {
                            result[i] = (float)samples[i];
                        }
                    }
                    
                    return result;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ReadAudioSamplesForLive 예외: {ex.Message}");
                return Array.Empty<float>();
            }
        }

        /// <summary>
        /// WAV 파일에서 지정된 시간 구간의 샘플을 읽습니다. (Double Precision)
        /// 원본 핑거프린트 생성과 완벽하게 동일한 정밀도를 보장하기 위해 double로 반환합니다.
        /// </summary>
        public static double[] ReadAudioSamplesForLiveDouble(string audioFilePath, TimeSpan startTime, int durationMs, out int sampleRate)
        {
            sampleRate = 0;
            
            try
            {
                var context = ParseWaveHeader(audioFilePath);
                sampleRate = context.SampleRate;
                int channels = context.Channels;
                int bytesPerSample = context.BitsPerSample / 8;
                
                // 시작 위치 계산 (모노 샘플 기준)
                long startSampleMono = (long)(startTime.TotalSeconds * sampleRate);
                long totalMonoSamples = channels == 2 ? context.TotalSamples / 2 : context.TotalSamples;
                
                if (startSampleMono >= totalMonoSamples)
                {
                    return Array.Empty<double>();
                }
                
                // 읽을 샘플 수 계산
                int samplesToRead = (int)(sampleRate * durationMs / 1000.0);
                if (startSampleMono + samplesToRead > totalMonoSamples)
                {
                    samplesToRead = (int)(totalMonoSamples - startSampleMono);
                }
                
                if (samplesToRead <= 0)
                {
                    return Array.Empty<double>();
                }
                
                // 파일에서 읽기
                using (var fs = File.Open(audioFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false))
                {
                    // 데이터 시작 위치로 이동
                    long fileStartPosition = context.DataStartPosition + (startSampleMono * channels * bytesPerSample);
                    reader.BaseStream.Seek(fileStartPosition, SeekOrigin.Begin);
                    
                    // 샘플 읽기 (스테레오인 경우 양 채널 모두 읽기)
                    int totalSamplesToRead = samplesToRead * channels;
                    var samples = new List<double>(totalSamplesToRead);
                    
                    for (int i = 0; i < totalSamplesToRead; i++)
                    {
                        long remainingBytes = reader.BaseStream.Length - reader.BaseStream.Position;
                        if (remainingBytes < bytesPerSample)
                        {
                            break;
                        }
                        
                        double sample = 0;
                        switch (context.BitsPerSample)
                        {
                            case 8:
                                sample = (reader.ReadByte() - 128) / 128.0;
                                break;
                            case 16:
                                sample = reader.ReadInt16() / 32768.0;
                                break;
                            case 24:
                                byte[] bytes24 = reader.ReadBytes(3);
                                if (bytes24.Length < 3) break;
                                int sample24 = (bytes24[0] | (bytes24[1] << 8) | (bytes24[2] << 16));
                                if ((sample24 & 0x800000) != 0)
                                {
                                    sample24 |= unchecked((int)0xFF000000);
                                }
                                sample = sample24 / 8388608.0;
                                break;
                            case 32:
                                if (context.AudioFormat == 1) // PCM
                                {
                                    sample = reader.ReadInt32() / 2147483648.0;
                                }
                                else // IEEE float
                                {
                                    sample = reader.ReadSingle();
                                }
                                break;
                        }
                        samples.Add(sample);
                    }
                    
                    // 스테레오인 경우 모노로 변환 (평균)
                    double[] result;
                    if (channels == 2)
                    {
                        result = new double[samples.Count / 2];
                        for (int i = 0; i < result.Length; i++)
                        {
                            if (i * 2 + 1 < samples.Count)
                            {
                                result[i] = (samples[i * 2] + samples[i * 2 + 1]) / 2.0;
                            }
                            else
                            {
                                result[i] = samples[i * 2];
                            }
                        }
                    }
                    else
                    {
                        result = samples.ToArray();
                    }
                    
                    return result;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ReadAudioSamplesForLiveDouble 예외: {ex.Message}");
                return Array.Empty<double>();
            }
        }

        #region 어려운 구간 감지 (Difficult Segment Detection)
        
        /// <summary>
        /// 오디오 샘플의 RMS (Root Mean Square) 값을 계산합니다.
        /// 낮은 RMS = 약한 오디오 신호 = "어려운 구간"
        /// </summary>
        /// <param name="samples">오디오 샘플 배열</param>
        /// <returns>RMS 값 (0~1 범위, 정규화된 오디오 기준)</returns>
        public static double CalculateRMS(float[] samples)
        {
            if (samples == null || samples.Length == 0) return 0;
            
            double sum = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                sum += samples[i] * samples[i];
            }
            return Math.Sqrt(sum / samples.Length);
        }

        /// <summary>
        /// RMS 임계값 (테스트 후 조정 필요)
        /// 약 -40dB 수준. 0.01 = 약한 신호, 0.001 = 거의 무음
        /// </summary>
        public static double MinRMSThreshold = 0.01;  // 오프라인 파일 추출용

        /// <summary>
        /// 실시간 오디오용 RMS 임계값
        /// 실시간 마이크 입력은 일반적으로 RMS가 낮으므로 별도 임계값 사용
        /// ★★★ 2026-02-05: 0.001 → 0.0001로 완화 (마이크 입력 약해도 처리) ★★★
        /// </summary>
        public static double MinRMSThresholdRealtime = 0.0001;  // 실시간 오디오 처리용

        /// <summary>
        /// 주어진 오디오 구간이 "어려운 구간"인지 판단합니다.
        /// 어려운 구간: 오디오 신호가 너무 약해서 매칭이 어려운 구간
        /// </summary>
        /// <param name="rms">RMS 값</param>
        /// <param name="isRealtime">실시간 오디오 여부</param>
        /// <returns>true: 어려운 구간 (시간 이동 권장)</returns>
        public static bool IsDifficultSegment(double rms, bool isRealtime = false)
        {
            double threshold = isRealtime ? MinRMSThresholdRealtime : MinRMSThreshold;
            return rms < threshold;
        }

        #endregion


        #region Live 핑거프린트 진단

        /// <summary>
        /// ★★★ Live vs 원본 핑거프린트 생성 방식 비교 진단 ★★★
        /// 영화 파일에서 직접 오디오를 추출하여 GenerateLiveFingerprint로 핑거프린트를 생성하고,
        /// 원본 핑거프린트와 비교하여 알고리즘 차이를 진단합니다.
        /// </summary>
        /// <param name="movieFilePath">영화 파일 경로</param>
        /// <param name="startTimeSec">시작 시간 (초)</param>
        /// <param name="durationSec">추출 길이 (초)</param>
        /// <param name="referenceIndex">원본 역인덱스</param>
        /// <param name="param">핑거프린트 설정</param>
        /// <param name="originalFpList">원본 FPT 리스트 (해시 비교용, 선택사항)</param>
        /// <param name="diagCallback">진단 메시지 콜백</param>
        /// <returns>진단 결과 (성공 여부, 매칭 결과, 집중도)</returns>
        public static (bool success, FingerprintMatchResult matchResult, double concentration) 
            DiagnoseLiveFingerprintGeneration(
                string movieFilePath,
                int startTimeSec,
                int durationSec,
                Dictionary<ulong, List<int>> referenceIndex,
                PickAudioFpParam param,
                List<FptEntry> originalFpList = null,
                Action<string> diagCallback = null)
        {
            diagCallback?.Invoke($"\n★★★ [Live vs 원본 핑거프린트 진단 시작] ★★★");
            diagCallback?.Invoke($"  영화 파일: {Path.GetFileName(movieFilePath)}");
            diagCallback?.Invoke($"  시작 시간: {TimeSpan.FromSeconds(startTimeSec):hh\\:mm\\:ss}");
            diagCallback?.Invoke($"  추출 길이: {durationSec}초");

            try
            {
                // 1. 영화 파일에서 오디오 추출
                diagCallback?.Invoke($"\n[Step 1] 영화 파일에서 오디오 추출 중...");
                float[] audioSamples = ExtractAudioFromMovie(movieFilePath, startTimeSec, durationSec, param.sampleRate, diagCallback);
                
                if (audioSamples == null || audioSamples.Length == 0)
                {
                    diagCallback?.Invoke($"  ❌ 오디오 추출 실패");
                    return (false, null, 0);
                }
                diagCallback?.Invoke($"  ✓ 추출된 샘플 수: {audioSamples.Length} ({audioSamples.Length / (double)param.sampleRate:F2}초)");

                // 2. GenerateLiveFingerprint로 핑거프린트 생성
                diagCallback?.Invoke($"\n[Step 2] GenerateLiveFingerprint로 핑거프린트 생성 중...");
                
                // ★★★ 2026-02-03: Peak 진단 추가 ★★★
                // double 배열 변환 (GenerateLiveFingerprint 내부와 동일)
                double[] samplesDouble = new double[audioSamples.Length];
                for (int i = 0; i < audioSamples.Length; i++) samplesDouble[i] = audioSamples[i];
                
                // Peak 직접 추출하여 주파수 분포 확인
                var peaksForDiag = ExtractPeaksFromSamples(samplesDouble, param);
                diagCallback?.Invoke($"  [Peak 진단] 추출된 Peak 수: {peaksForDiag.Count}개");
                
                // 주파수 분포 분석
                var freqDistribution = peaksForDiag
                    .GroupBy(p => ((int)(p.Frequency / 500)) * 500) // 500Hz 단위로 그룹화
                    .OrderBy(g => g.Key)
                    .Take(10);
                diagCallback?.Invoke($"  [Peak 진단] 주파수 분포 (500Hz 단위):");
                foreach (var grp in freqDistribution)
                {
                    diagCallback?.Invoke($"    {grp.Key}~{grp.Key + 500}Hz: {grp.Count()}개");
                }
                
                // 샘플 Peak 출력 (시간순으로 정렬하여 처음 10개)
                var sortedPeaks = peaksForDiag.OrderBy(p => p.Time).ThenBy(p => p.Frequency).ToList();
                diagCallback?.Invoke($"  [Peak 진단] 샘플 Peak 10개 (시간순):");
                foreach (var peak in sortedPeaks.Take(10))
                {
                    diagCallback?.Invoke($"    Freq={peak.Frequency:F1}Hz, Time={peak.Time:F3}s, Mag={peak.Magnitude:E2}");
                }
                
                // ★★★ 2026-02-03: 시간 분포 진단 추가 ★★★
                var timeDistribution = peaksForDiag
                    .GroupBy(p => (int)(p.Time)) // 1초 단위로 그룹화
                    .OrderBy(g => g.Key);
                diagCallback?.Invoke($"  [Peak 진단] 시간 분포 (1초 단위):");
                foreach (var grp in timeDistribution)
                {
                    diagCallback?.Invoke($"    {grp.Key}~{grp.Key + 1}초: {grp.Count()}개");
                }
                
                // ★★★ 2026-02-03: Live Peak 주파수 목록 ★★★
                var livePeakFreqs = sortedPeaks.Select(p => (int)p.Frequency).Distinct().OrderBy(f => f).ToList();
                diagCallback?.Invoke($"\n  [Live Peak 전체 주파수 목록 (처음 20개)]:");
                diagCallback?.Invoke($"    {string.Join(", ", livePeakFreqs.Take(20))}Hz");
                
                var liveFp = GenerateLiveFingerprint(audioSamples, param);
                
                if (liveFp == null || liveFp.Count == 0)
                {
                    diagCallback?.Invoke($"  ❌ 핑거프린트 생성 실패");
                    return (false, null, 0);
                }
                
                int totalHashes = liveFp.Sum(e => e.Hashes?.Count ?? 0);
                diagCallback?.Invoke($"  ✓ 생성된 엔트리: {liveFp.Count}개, 총 해시: {totalHashes}개");
                diagCallback?.Invoke($"  ✓ 해시/초: {totalHashes / (double)durationSec:F1}개");

                // 3. 타임스탬프 보정 (startTimeSec 기준으로)
                diagCallback?.Invoke($"\n[Step 3] 타임스탬프 보정 중...");
                foreach (var entry in liveFp)
                {
                    // 실제 영화 시간 기준으로 타임스탬프 설정
                    entry.Timestamp = startTimeSec + entry.Timestamp;
                }
                diagCallback?.Invoke($"  ✓ 타임스탬프 범위: {liveFp.First().Timestamp} ~ {liveFp.Last().Timestamp}초");

                // 4. CalcOffsetConcentration으로 집중도 계산
                diagCallback?.Invoke($"\n[Step 4] 오프셋 집중도 계산 중...");
                var (calcResult, concentration) = CalcOffsetConcentration(liveFp, referenceIndex, maxHashOccurrences: DefaultMaxHashOccurrences);
                diagCallback?.Invoke($"  ✓ 집중도: {concentration:P2}");

                // 5. MatchFingerprints로 매칭
                // ★ 신뢰도 임계값 0.2: 집중도가 낮은 환경에서도 매칭 허용 ★
                diagCallback?.Invoke($"\n[Step 5] 원본 핑거프린트와 매칭 중...");
                var matchResult = MatchFingerprints(liveFp, null, referenceIndex, minConfidence: 0.2, maxHashOccurrences: DefaultMaxHashOccurrences);
                
                if (matchResult.IsMatched)
                {
                    // ★★★ 2026-02-03: 오프셋 → 원본 매칭 시간 변환 ★★★
                    // MatchedTime에는 오프셋(refTs - liveTs)이 저장됨
                    // 원본 매칭 시간 = Live 시작 시간 + 오프셋
                    int offsetSec = (int)matchResult.MatchedTime.TotalSeconds;
                    TimeSpan actualMatchedTime = TimeSpan.FromSeconds(startTimeSec + offsetSec);
                    
                    diagCallback?.Invoke($"  ✓ 매칭 성공!");
                    diagCallback?.Invoke($"    매칭 시간: {actualMatchedTime:hh\\:mm\\:ss}");
                    diagCallback?.Invoke($"    예상 시간: {TimeSpan.FromSeconds(startTimeSec):hh\\:mm\\:ss}");
                    diagCallback?.Invoke($"    오프셋: {offsetSec}초 (정확할수록 0에 가까움)");
                    diagCallback?.Invoke($"    신뢰도: {matchResult.Confidence:P1}");
                }
                else
                {
                    diagCallback?.Invoke($"  ❌ 매칭 실패 (신뢰도: {matchResult.Confidence:P1})");
                }

                // 6. 해시 비교 진단 (원본 vs Live)
                diagCallback?.Invoke($"\n[Step 6] 해시 일치율 분석...");
                int matchedHashCount = 0;
                int totalLiveHashes = 0;
                
                // Live 해시 샘플 수집 (처음 10개)
                var liveHashSamples = new List<string>();
                
                foreach (var entry in liveFp)
                {
                    if (entry.Hashes == null) continue;
                    foreach (var hash in entry.Hashes)
                    {
                        if (string.IsNullOrEmpty(hash.Hash)) continue;
                        totalLiveHashes++;
                        
                        if (liveHashSamples.Count < 10)
                        {
                            liveHashSamples.Add(hash.Hash);
                        }
                        
                        ulong hashValue = FingerprintHashData_mp.HexStringToUlong(hash.Hash);
                        if (hashValue != 0UL && referenceIndex.ContainsKey(hashValue))
                        {
                            matchedHashCount++;
                        }
                    }
                }
                double matchRate = totalLiveHashes > 0 ? (double)matchedHashCount / totalLiveHashes : 0;
                diagCallback?.Invoke($"  Live 해시 수: {totalLiveHashes}");
                diagCallback?.Invoke($"  역인덱스에 존재하는 해시: {matchedHashCount} ({matchRate:P1})");
                
                // ★★★ 2026-02-03: 해시 형식 진단 추가 ★★★
                diagCallback?.Invoke($"\n[Step 7] 해시 형식 비교 진단...");
                
                // ★★★ 원본 FPT의 해당 시간대 해시 직접 출력 ★★★
                if (originalFpList != null && originalFpList.Count > 0)
                {
                    diagCallback?.Invoke($"\n  [원본 FPT 해시 ({startTimeSec}초 위치)]");
                    var originalEntries = originalFpList.Where(e => e.Timestamp >= startTimeSec && e.Timestamp <= startTimeSec + 2).ToList();
                    diagCallback?.Invoke($"    해당 범위 엔트리: {originalEntries.Count}개");
                    
                    if (originalEntries.Count > 0)
                    {
                        // 첫 번째 엔트리의 Peak 주파수들 출력
                        var firstEntry = originalEntries.First();
                        diagCallback?.Invoke($"    [Timestamp={firstEntry.Timestamp}초] 해시 {firstEntry.Hashes?.Count ?? 0}개");
                        
                        if (firstEntry.Hashes != null && firstEntry.Hashes.Count > 0)
                        {
                            // F1 주파수 목록 추출
                            var origF1Freqs = firstEntry.Hashes.Select(h => (int)h.Frequency1).Distinct().OrderBy(f => f).ToList();
                            diagCallback?.Invoke($"    [원본 F1 주파수 목록 (처음 20개)]: {string.Join(", ", origF1Freqs.Take(20))}Hz");
                            
                            // 처음 5개 해시 출력
                            diagCallback?.Invoke($"    [원본 해시 샘플 5개]:");
                            foreach (var hash in firstEntry.Hashes.Take(5))
                            {
                                diagCallback?.Invoke($"      '{hash.Hash}' (F1={hash.Frequency1:F0}, F2={hash.Frequency2:F0}, dt={hash.TimeDelta:F3})");
                            }
                        }
                    }
                    else
                    {
                        diagCallback?.Invoke($"    ⚠️ 해당 시간대에 원본 FPT 엔트리가 없습니다!");
                    }
                }
                else
                {
                    diagCallback?.Invoke($"\n  [원본 FPT 비교] 원본 FPT 리스트가 전달되지 않음");
                }
                
                // ★★★ 2026-02-03: 다양한 Peak 쌍의 해시 출력 (중복 F1/F2 제거) ★★★
                diagCallback?.Invoke($"\n  [Live 해시 - 다양한 Peak 쌍 10개]");
                var uniquePeakPairs = new HashSet<(int, int)>();
                int liveHashIdx = 0;
                foreach (var entry in liveFp)
                {
                    if (entry.Hashes == null) continue;
                    foreach (var hash in entry.Hashes)
                    {
                        int f1 = (int)hash.Frequency1;
                        int f2 = (int)hash.Frequency2;
                        var pair = (f1, f2);
                        
                        // 이미 출력한 Peak 쌍은 건너뛰기
                        if (uniquePeakPairs.Contains(pair)) continue;
                        uniquePeakPairs.Add(pair);
                        
                        if (liveHashIdx >= 10) break;
                        diagCallback?.Invoke($"    '{hash.Hash}' (F1={f1}, F2={f2}, dt={hash.TimeDelta:F3})");
                        liveHashIdx++;
                    }
                    if (liveHashIdx >= 10) break;
                }
                diagCallback?.Invoke($"    → 총 {uniquePeakPairs.Count}개의 고유 Peak 쌍");
                
                // ★★★ 일치하는 해시의 Peak 정보 출력 ★★★
                diagCallback?.Invoke($"\n  [역인덱스와 일치하는 Live 해시 - 처음 10개]");
                int matchedHashIdx = 0;
                foreach (var entry in liveFp)
                {
                    if (entry.Hashes == null) continue;
                    foreach (var hash in entry.Hashes)
                    {
                        if (string.IsNullOrEmpty(hash.Hash)) continue;
                        ulong hashValue = FingerprintHashData_mp.HexStringToUlong(hash.Hash);
                        if (hashValue != 0UL && referenceIndex.ContainsKey(hashValue))
                        {
                            if (matchedHashIdx >= 10) break;
                            var timestamps = referenceIndex[hashValue];
                            diagCallback?.Invoke($"    ✓ '{hash.Hash}' (F1={hash.Frequency1:F0}, F2={hash.Frequency2:F0}) → 원본 {timestamps.Count}개 위치: [{string.Join(", ", timestamps.Take(5))}초...]");
                            matchedHashIdx++;
                        }
                    }
                    if (matchedHashIdx >= 10) break;
                }
                if (matchedHashIdx == 0)
                {
                    diagCallback?.Invoke($"    ⚠️ 일치하는 해시가 없습니다!");
                }
                
                // 역인덱스에서 샘플 해시 추출
                diagCallback?.Invoke($"\n  [역인덱스 해시 샘플 10개]");
                int refSampleCount = 0;
                foreach (var kvp in referenceIndex)
                {
                    if (refSampleCount >= 10) break;
                    string refHashStr = kvp.Key.ToString("X16"); // 16자리 대문자 Hex
                    diagCallback?.Invoke($"    '{refHashStr}' (ulong: {kvp.Key}, 타임스탬프 수: {kvp.Value.Count})");
                    refSampleCount++;
                }
                
                // 해시 길이 통계
                var hashLengths = liveHashSamples.Select(h => h.Length).Distinct().ToList();
                diagCallback?.Invoke($"\n  [해시 형식 분석]");
                diagCallback?.Invoke($"    Live 해시 길이: {string.Join(", ", hashLengths)}자리");
                diagCallback?.Invoke($"    역인덱스 해시: 16자리 (ulong → X16 변환)");
                
                if (liveHashSamples.Any() && liveHashSamples.First().Length != 16)
                {
                    diagCallback?.Invoke($"    ⚠️ 경고: Live 해시와 역인덱스 해시 형식이 다릅니다!");
                    diagCallback?.Invoke($"    → 해시 문자열 비교 시 형식 차이로 불일치 발생 가능");
                }

                diagCallback?.Invoke($"\n★★★ [Live vs 원본 핑거프린트 진단 끝] ★★★\n");

                return (true, matchResult, concentration);
            }
            catch (Exception ex)
            {
                diagCallback?.Invoke($"  ❌ 예외 발생: {ex.Message}");
                diagCallback?.Invoke($"★★★ [Live vs 원본 핑거프린트 진단 끝 - 실패] ★★★\n");
                return (false, null, 0);
            }
        }

        /// <summary>
        /// 영화 파일에서 특정 구간의 오디오를 추출합니다.
        /// </summary>
        private static float[] ExtractAudioFromMovie(string movieFilePath, int startTimeSec, int durationSec, int targetSampleRate, Action<string> diagCallback = null)
        {
            try
            {
                using (var reader = new NAudio.Wave.AudioFileReader(movieFilePath))
                {
                    // 원본 정보
                    int sourceSampleRate = reader.WaveFormat.SampleRate;
                    int channels = reader.WaveFormat.Channels;
                    diagCallback?.Invoke($"  원본: {sourceSampleRate}Hz, {channels}ch");

                    // ★★★ 2026-02-03 v4: 프레임 그리드 정렬 - Ceiling 사용 ★★★
                    // 원본 FPT의 Timestamp는 Peak.Time의 floor() 값입니다.
                    // Timestamp=60초의 해시는 프레임 time >= 60.0초인 프레임에서 생성됩니다.
                    // 
                    // 문제: floor 사용 시 프레임 1406 (59.989초) → Timestamp=59
                    // 해결: ceiling 사용 → 프레임 1407 (60.032초) → Timestamp=60
                    // 
                    // 계산:
                    // - 프레임 인덱스 = ceil(60초 × 48000 / 2048) = ceil(1406.25) = 1407
                    // - 프레임 시작 = 1407 × 2048 = 2,881,536 샘플 = 60.032000초
                    const int defaultHopSize = 2048; // FingerprintConfig 기본값
                    long startSample = (long)startTimeSec * sourceSampleRate;
                    long frameIndex = (long)Math.Ceiling((double)startSample / defaultHopSize); // ceiling으로 변경!
                    long alignedStartSample = frameIndex * defaultHopSize;
                    
                    double alignedStartTime = (double)alignedStartSample / sourceSampleRate;
                    
                    diagCallback?.Invoke($"  [프레임 그리드 정렬 - Ceiling]");
                    diagCallback?.Invoke($"    요청: {startTimeSec}초 → 정렬: {alignedStartTime:F6}초 (프레임 {frameIndex})");
                    
                    reader.CurrentTime = TimeSpan.FromSeconds(alignedStartTime);

                    // 읽을 샘플 수 계산
                    int samplesToRead = durationSec * sourceSampleRate * channels;
                    float[] buffer = new float[samplesToRead];
                    int samplesRead = reader.Read(buffer, 0, samplesToRead);
                    
                    diagCallback?.Invoke($"  읽은 샘플: {samplesRead} (요청: {samplesToRead})");

                    // 모노 변환
                    float[] monoSamples;
                    if (channels > 1)
                    {
                        int monoLength = samplesRead / channels;
                        monoSamples = new float[monoLength];
                        for (int i = 0; i < monoLength; i++)
                        {
                            float sum = 0;
                            for (int ch = 0; ch < channels; ch++)
                            {
                                sum += buffer[i * channels + ch];
                            }
                            monoSamples[i] = sum / channels;
                        }
                    }
                    else
                    {
                        monoSamples = buffer;
                    }
                    diagCallback?.Invoke($"  모노 변환: {monoSamples.Length} 샘플");

                    // 리샘플링 (필요시)
                    if (sourceSampleRate != targetSampleRate)
                    {
                        diagCallback?.Invoke($"  리샘플링: {sourceSampleRate}Hz → {targetSampleRate}Hz");
                        monoSamples = ResampleAudio(monoSamples, sourceSampleRate, targetSampleRate);
                        diagCallback?.Invoke($"  리샘플링 후: {monoSamples.Length} 샘플");
                    }

                    return monoSamples;
                }
            }
            catch (Exception ex)
            {
                diagCallback?.Invoke($"  오디오 추출 오류: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 간단한 선형 보간 리샘플링
        /// </summary>
        private static float[] ResampleAudio(float[] source, int sourceSampleRate, int targetSampleRate)
        {
            double ratio = (double)sourceSampleRate / targetSampleRate;
            int targetLength = (int)(source.Length / ratio);
            float[] result = new float[targetLength];

            for (int i = 0; i < targetLength; i++)
            {
                double srcIndex = i * ratio;
                int idx = (int)srcIndex;
                double frac = srcIndex - idx;

                if (idx + 1 < source.Length)
                {
                    result[i] = (float)(source[idx] * (1 - frac) + source[idx + 1] * frac);
                }
                else if (idx < source.Length)
                {
                    result[i] = source[idx];
                }
            }

            return result;
        }

        #endregion


        /// <summary>
        /// 라이브 오디오 샘플에서 실시간 핑거프린트를 생성합니다.
        /// </summary>
        public static List<FptEntry> GenerateLiveFingerprint(float[] audioSamples, PickAudioFpParam param)
        {
            if (audioSamples == null || audioSamples.Length == 0)
            {
                return new List<FptEntry>();
            }

            // double 배열로 변환
            double[] samples = new double[audioSamples.Length];
            for (int i = 0; i < audioSamples.Length; i++)
            {
                samples[i] = audioSamples[i];
            }

            // Peak 추출
            var peaks = ExtractPeaksFromSamples(samples, param);
            
            // ★★★ 2026-02-06: Peak 필터링은 GenerateFingerprints에서 통합 처리 ★★★
            // 이중 필터링 방지 (60% x 60% = 36% 문제 해결)
            
            if (peaks.Count == 0)
            {
                return new List<FptEntry>();
            }

            // ★ Live 핑거프린트 생성: forIndexing=false (단일 해시만 생성) ★
            return GenerateFingerprints(peaks, param.sampleRate, forIndexing: false);
        }

        /// <summary>
        /// 라이브 오디오 샘플에서 실시간 핑거프린트를 생성하고 품질 정보를 반환합니다.
        /// </summary>
        /// <param name="audioSamples">오디오 샘플</param>
        /// <param name="sampleRate">샘플 레이트</param>
        /// <param name="fftSize">FFT 크기</param>
        /// <param name="hopSize">Hop 크기</param>
        /// <param name="qualityScore">품질 점수 (출력)</param>
        /// <param name="peakCount">Peak 개수 (출력)</param>
        /// <returns>핑거프린트 엔트리 리스트</returns>
        public static List<FptEntry> GenerateLiveFingerprintWithQuality(
            float[] audioSamples,
            PickAudioFpParam param,
            out double qualityScore,
            out int peakCount)
        {
            qualityScore = 0.0;
            peakCount = 0;

            if (audioSamples == null || audioSamples.Length == 0)
            {
                return new List<FptEntry>();
            }

            // double 배열로 변환
            double[] samples = new double[audioSamples.Length];
            for (int i = 0; i < audioSamples.Length; i++)
            {
                samples[i] = audioSamples[i];
            }

            // Peak 추출
            var peaks = ExtractPeaksFromSamples(samples, param);
            
            // ★★★ 2026-02-06: Peak 필터링은 GenerateFingerprints에서 통합 처리 ★★★
            // 이중 필터링 방지 (60% x 60% = 36% 문제 해결)
            
            peakCount = peaks.Count;
            
            if (peaks.Count == 0)
            {
                return new List<FptEntry>();
            }

            // 품질 점수 계산 (peak 개수 포함)
            // 전체 구간에 대한 평균 SNR과 Spectral 특징은 근사값 사용
            // 실제로는 peak 개수 기반으로 품질 점수 계산
            // 간단한 근사: peak 개수가 많을수록 품질이 높다고 가정
            double avgSnrDb = 10.0; // 기본값 (실제로는 계산 필요하지만 복잡하므로 근사)
            double avgSpectralEntropy = 2.0; // 기본값
            double avgPeakSharpness = 50.0; // 기본값
            
            qualityScore = CalculateQualityScore(peakCount, avgSnrDb, avgSpectralEntropy, avgPeakSharpness);

            // ★★★ Live 해시 생성 진단 ★★★
            double audioDurationSec = audioSamples.Length / (double)param.sampleRate;
            System.Diagnostics.Debug.WriteLine($"\n★★★ [Live 해시 생성 진단 시작] ★★★");
            System.Diagnostics.Debug.WriteLine($"  오디오 길이: {audioDurationSec:F2}초");
            System.Diagnostics.Debug.WriteLine($"  Peak 수: {peaks.Count}개 ({peaks.Count / audioDurationSec:F1} Peak/초)");
            
            // Dynamic Fan-out 통계 리셋
            ResetFanOutDiagnostics();
            
            // ★ Live 핑거프린트 생성: forIndexing=false (단일 해시만 생성) ★
            var fingerprints = GenerateFingerprints(peaks, param.sampleRate, forIndexing: false);
            
            // Dynamic Fan-out 통계 출력
            PrintFanOutDiagnostics();
            
            // 생성된 해시 수 계산
            int totalHashCount = 0;
            foreach (var entry in fingerprints)
            {
                if (entry.Hashes != null)
                {
                    totalHashCount += entry.Hashes.Count;
                }
            }
            double hashPerSec = audioDurationSec > 0 ? totalHashCount / audioDurationSec : 0;
            System.Diagnostics.Debug.WriteLine($"  총 해시 수: {totalHashCount}개 ({hashPerSec:F1} hash/초)");
            System.Diagnostics.Debug.WriteLine($"  ★ 비교: 원본은 약 256 hash/초 ★");
            System.Diagnostics.Debug.WriteLine($"★★★ [Live 해시 생성 진단 끝] ★★★\n");
            
            return fingerprints;
        }

        /// <summary>
        /// 라이브 오디오 샘플에서 실시간 핑거프린트를 생성하고 품질 정보를 반환합니다. (Double Precision Overload)
        /// </summary>
        public static List<FptEntry> GenerateSampleFingerprintWithQuality(
            double[] audioSamples,
            PickAudioFpParam param,
            out double qualityScore,
            out int peakCount)
        {
            qualityScore = 0.0;
            peakCount = 0;

            if (audioSamples == null || audioSamples.Length == 0)
            {
                return new List<FptEntry>();
            }

            // Peak 추출 (Double 배열 직접 사용)
            var peaks = ExtractPeaksFromSamples(audioSamples, param);
            
            // ★★★ 2026-02-06: Peak 필터링은 GenerateFingerprints에서 통합 처리 ★★★
            // 이중 필터링 방지 (60% x 60% = 36% 문제 해결)
            
            peakCount = peaks.Count;
            
            if (peaks.Count == 0)
            {
                return new List<FptEntry>();
            }

            // 품질 점수 계산
            double avgSnrDb = 10.0;
            double avgSpectralEntropy = 2.0;
            double avgPeakSharpness = 50.0;
            
            qualityScore = CalculateQualityScore(peakCount, avgSnrDb, avgSpectralEntropy, avgPeakSharpness);

            // ★★★ Live 해시 생성 진단 (Double Precision Overload) ★★★
            double audioDurationSec = audioSamples.Length / (double)param.sampleRate;
            System.Diagnostics.Debug.WriteLine($"\n★★★ [Live 해시 생성 진단 시작] ★★★");
            System.Diagnostics.Debug.WriteLine($"  오디오 길이: {audioDurationSec:F2}초");
            System.Diagnostics.Debug.WriteLine($"  Peak 수: {peaks.Count}개 ({peaks.Count / audioDurationSec:F1} Peak/초)");
            
            // Dynamic Fan-out 통계 리셋
            ResetFanOutDiagnostics();
            
            // ★ Live 핑거프린트 생성: forIndexing=false (단일 해시만 생성) ★
            var fingerprints = GenerateFingerprints(peaks, param.sampleRate, forIndexing: false);
            
            // Dynamic Fan-out 통계 출력
            PrintFanOutDiagnostics();
            
            // 생성된 해시 수 계산
            int totalHashCount = 0;
            foreach (var entry in fingerprints)
            {
                if (entry.Hashes != null)
                {
                    totalHashCount += entry.Hashes.Count;
                }
            }
            double hashPerSec = audioDurationSec > 0 ? totalHashCount / audioDurationSec : 0;
            System.Diagnostics.Debug.WriteLine($"  총 해시 수: {totalHashCount}개 ({hashPerSec:F1} hash/초)");
            System.Diagnostics.Debug.WriteLine($"  ★ 비교: 원본은 약 256 hash/초 ★");
            System.Diagnostics.Debug.WriteLine($"★★★ [Live 해시 생성 진단 끝] ★★★\n");
            
            return fingerprints;
        }

        /// <summary>
        /// 프레임의 품질 점수를 계산합니다. (0~100점)
        /// SNR, 평균 크기, 최대 크기 등을 종합적으로 고려합니다.
        /// </summary>
        private static double CalculateFrameQualityScore(double[] magnitudes, double snrDb, double avgMagnitude, double maxMagnitude)
        {
            // 1. SNR 점수 (0~40점)
            // 10dB 이하: 0점, 40dB 이상: 40점
            double snrScore = Math.Min(40, Math.Max(0, (snrDb - 10) * (40.0 / 30.0)));

            // 2. Magnitude 점수 (0~40점)
            // 로그 스케일로 변환하여 평가 (-100dB ~ -20dB)
            double logMaxMag = 10 * Math.Log10(maxMagnitude + double.Epsilon);
            double magScore = Math.Min(40, Math.Max(0, (logMaxMag + 100) * (40.0 / 80.0)));

            // 3. Peak 선명도 점수 (0~20점)
            // 최대값 대 평균값 비율 (Peak-to-Average Ratio)
            double par = (avgMagnitude > 0) ? maxMagnitude / avgMagnitude : 0;
            double parScore = Math.Min(20, Math.Max(0, (par - 5) * (20.0 / 45.0))); // PAR 5~50

            return snrScore + magScore + parScore;
        }

        /// <summary>
        /// 오디오 샘플에서 Peak를 추출합니다.
        /// </summary>
        private static List<Peak> ExtractPeaksFromSamples(double[] samples, PickAudioFpParam param)
        {
            var peaksBag = new ConcurrentBag<Peak>();
            int sampleRate = param.sampleRate;
            int fftSize = param.fptCfg.FFTSize;
            int hopSize = param.fptCfg.HopSize;
            if (samples.Length < fftSize)
            {
                return new List<Peak>();
            }

            double QualThr = param.QualityThreshold;
            // 짧은 구간(250ms 이하)에서는 필터링 조건 완화
            double durationSeconds = samples.Length / (double)sampleRate;
            bool isShortSegment = durationSeconds <= 0.25; // 250ms 이하를 짧은 구간으로 간주
            double effectiveSNRThreshold = isShortSegment ? MinSNRThresholdDb - 5.0 : MinSNRThresholdDb; // 짧은 구간에서는 -8dB로 완화
            double effectiveQualityThreshold = isShortSegment ? QualThr - 25.0 : QualThr; // 짧은 구간에서는 35점으로 완화

            // 윈도우 함수 사전 계산
            double[] hammingWindow = new double[fftSize];
            for (int i = 0; i < fftSize; i++)
            {
                hammingWindow[i] = fftSize > 1 ? 0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (fftSize - 1)) : 1.0;
            }

            int spectrumLength = fftSize / 2;
            double[] frequencies = new double[spectrumLength];
            for (int i = 0; i < spectrumLength; i++)
            {
                frequencies[i] = i * sampleRate / (double)fftSize;
            }

            int totalFrames = (int)Math.Ceiling((samples.Length - fftSize) / (double)hopSize) + 1;
            
            for (int frameIndex = 0; frameIndex < totalFrames; frameIndex++)
            {
                int startIndex = frameIndex * hopSize;
                if (startIndex + fftSize > samples.Length)
                {
                    break;
                }

                double time = startIndex / (double)sampleRate;
                
                // 프레임 추출
                double[] frame = new double[fftSize];
                Array.Copy(samples, startIndex, frame, 0, fftSize);

                // FFT 수행
                double[] real = new double[fftSize];
                double[] imag = new double[fftSize];
                for (int i = 0; i < fftSize; i++)
                {
                    real[i] = frame[i] * hammingWindow[i];
                    imag[i] = 0;
                }
                FFT(real, imag);

                // 스펙트럼 계산 (원본과 동일하게 파워 스펙트럼 사용)
                double[] magnitudes = new double[spectrumLength];
                double maxMagnitude = 0;
                double sumMagnitude = 0;
                for (int i = 0; i < spectrumLength; i++)
                {
                    double re = real[i];
                    double im = imag[i];
                    magnitudes[i] = re * re + im * im; // 제곱값 (파워 스펙트럼) - 원본과 동일
                    if (magnitudes[i] > maxMagnitude)
                    {
                        maxMagnitude = magnitudes[i];
                    }
                    sumMagnitude += magnitudes[i];
                }

                if (maxMagnitude <= 0)
                {
                    continue;
                }

                // ★ 최소 크기 제한 (노이즈 제거) ★
                if (param.minMagnitude > 0 && maxMagnitude < param.minMagnitude)
                {
                    continue;
                }

                // 평균값 계산
                double avgMagnitude = spectrumLength > 0 ? sumMagnitude / spectrumLength : 0;

                // ★★★ 2026-02-03: SNR/품질 필터링 비활성화 ★★★
                // 문제: 원본 FPT(ProcessFrame)는 SNR/품질 필터링 없이 모든 프레임에서 Peak 추출
                //       Live(ExtractPeaksFromSamples)는 SNR/품질 필터링으로 일부 프레임 건너뜀
                //       → 같은 오디오에서 다른 Peak 추출 → 해시 불일치 발생!
                // 해결: 원본과 동일하게 필터링 없이 모든 프레임 처리
                /*
                double snrDb = EstimateSNRFrame(magnitudes);
                if (snrDb < effectiveSNRThreshold) continue;

                // 품질 점수 기반 스킵
                if (param.UseQualityBasedFiltering)
                {
                    double qualityScore = CalculateFrameQualityScore(magnitudes, snrDb, avgMagnitude, maxMagnitude);
                    if (qualityScore < effectiveQualityThreshold) continue;
                }
                */

                // Peak 검출 (Thread-safe 함수 사용)
                ImprovedPeakDetection.DetectPeaksAdaptive(peaksBag, magnitudes, frequencies, time, param.fptCfg);
            }

            return peaksBag.ToList();
        }

        public static (bool, double) CalcOffsetConcentration(
            List<FptEntry> liveFpts,
            Dictionary<ulong, List<int>> referenceIndex,
            int maxHashOccurrences = DefaultMaxHashOccurrences,  // 통합 상수 사용
            List<FptEntry> originalFpts = null)  // 원본 핑거프린트 (해시 비교 진단용)
        {
            double OffsetConcentration = 0.0;
            var diag = new MatchDiagnostics
            {
                OffsetHistogram = new Dictionary<int, int>(),
                TopOffsets = new List<(int, int)>()
            };

            if (liveFpts == null || liveFpts.Count == 0)
                return (false, OffsetConcentration);

            if (referenceIndex == null || referenceIndex.Count == 0)
                return (false, OffsetConcentration);

            int totalHashes = 0;
            int matchedHashes = 0;
            int filteredHashes = 0;  // 필터링된 해시 수

            // 라이브 핑거프린트의 각 해시를 기준 역인덱스에서 검색
            foreach (var entry in liveFpts)
            {
                if (entry.Hashes == null) continue;

                foreach (var hash in entry.Hashes)
                {
                    if (string.IsNullOrEmpty(hash.Hash)) continue;

                    totalHashes++;
                    ulong hashValue = FingerprintHashData_mp.HexStringToUlong(hash.Hash);

                    if (hashValue == 0UL) continue;

                    if (referenceIndex.TryGetValue(hashValue, out var refTimestamps))
                    {
                        // ★ 과다 출현 해시 필터링 ★
                        if (refTimestamps.Count > maxHashOccurrences)
                        {
                            filteredHashes++;
                            continue;  // 이 해시는 건너뜀
                        }

                        matchedHashes++;

                        foreach (var refTs in refTimestamps)
                        {
                            int offset = refTs - entry.Timestamp;

                            if (!diag.OffsetHistogram.ContainsKey(offset))
                                diag.OffsetHistogram[offset] = 0;

                            diag.OffsetHistogram[offset]++;
                        }
                    }
                }
            }

            diag.TotalLiveHashes = totalHashes;
            diag.UniqueMatchedHashes = matchedHashes;

            // 상위 10개 오프셋
            diag.TopOffsets = diag.OffsetHistogram
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();

            // ★ 수정된 오프셋 집중도 계산 ★
            // ★★★ 2026-02-05: Loopback 타이밍 변형 흡수를 위해 ±2초로 확대 ★★★
            // N±2로 분산된 매칭을 합산하여 평가
            if (diag.TopOffsets.Count > 0 && matchedHashes > 0)
            {
                var top = diag.TopOffsets[0];
                int bestOffset = top.Offset;
                int mergedCount = top.Count;
                
                // 인접 오프셋(±2) 확인 및 합산
                for (int delta = -2; delta <= 2; delta++)
                {
                    if (delta == 0) continue;
                    if (diag.OffsetHistogram.TryGetValue(bestOffset + delta, out int adjCount))
                        mergedCount += adjCount;
                }
                
                OffsetConcentration = (double)mergedCount / matchedHashes;
            }

            // ★ 디버그: 오프셋 히스토그램 상세 로그 ★
            System.Diagnostics.Debug.WriteLine($"\n★★★ [Offset Concentration 진단] ★★★");
            System.Diagnostics.Debug.WriteLine($"  Live 해시 총 개수: {totalHashes}");
            System.Diagnostics.Debug.WriteLine($"  매칭된 해시 개수: {matchedHashes} ({(totalHashes > 0 ? (100.0 * matchedHashes / totalHashes) : 0):F1}%)");
            System.Diagnostics.Debug.WriteLine($"  필터링된 해시 개수: {filteredHashes}");
            System.Diagnostics.Debug.WriteLine($"  오프셋 히스토그램 크기: {diag.OffsetHistogram.Count}개 고유 오프셋");
            System.Diagnostics.Debug.WriteLine($"  Offset Concentration: {OffsetConcentration:F4}");
            
            if (diag.TopOffsets.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine($"\n  [Top 10 오프셋 (매칭 횟수순)]");
                foreach (var (offset, count) in diag.TopOffsets)
                {
                    double percentage = matchedHashes > 0 ? (100.0 * count / matchedHashes) : 0;
                    TimeSpan ts = TimeSpan.FromSeconds(Math.Abs(offset));
                    string sign = offset >= 0 ? "+" : "-";
                    System.Diagnostics.Debug.WriteLine($"    오프셋 {sign}{ts:hh\\:mm\\:ss} ({offset}초): {count}회 ({percentage:F1}%)");
                }
                
                // 집중도 계산 상세 (±2초 병합)
                var top = diag.TopOffsets[0];
                int bestOffset = top.Offset;
                int mergedCount2 = top.Count;
                for (int d = -2; d <= 2; d++)
                {
                    if (d == 0) continue;
                    if (diag.OffsetHistogram.TryGetValue(bestOffset + d, out int adj)) mergedCount2 += adj;
                }
                System.Diagnostics.Debug.WriteLine($"\n  [집중도 계산 상세]");
                System.Diagnostics.Debug.WriteLine($"    최고 오프셋: {bestOffset}초 = {top.Count}회");
                System.Diagnostics.Debug.WriteLine($"    인접 오프셋 병합 (±2초): {mergedCount2}회");
                System.Diagnostics.Debug.WriteLine($"    집중도 = {mergedCount2} / {matchedHashes} = {OffsetConcentration:F4}");
            }
            System.Diagnostics.Debug.WriteLine($"★★★ [Offset Concentration 진단 끝] ★★★\n");

            // ★★★ 해시 비교 진단: 원본 핑거프린트가 제공된 경우에만 실행 ★★★
            if (originalFpts != null && diag.TopOffsets.Count > 0)
            {
                var topOffset = diag.TopOffsets[0].Offset;  // 최고 오프셋
                
                System.Diagnostics.Debug.WriteLine($"\n★★★ [해시 직접 비교 진단] ★★★");
                System.Diagnostics.Debug.WriteLine($"  최고 오프셋: {topOffset}초 (Live 0초 → 원본 {topOffset}초)");
                
                // Live 0-3초 구간의 해시 수집
                var liveHashSet = new HashSet<string>();
                var liveHashList = new List<string>();
                foreach (var entry in liveFpts)
                {
                    if (entry.Timestamp >= 0 && entry.Timestamp <= 3 && entry.Hashes != null)
                    {
                        foreach (var hash in entry.Hashes)
                        {
                            if (!string.IsNullOrEmpty(hash.Hash))
                            {
                                liveHashSet.Add(hash.Hash);
                                liveHashList.Add(hash.Hash);
                            }
                        }
                    }
                }
                
                // 원본 topOffset ~ topOffset+3초 구간의 해시 수집
                var origHashSet = new HashSet<string>();
                var origHashList = new List<string>();
                foreach (var entry in originalFpts)
                {
                    if (entry.Timestamp >= topOffset && entry.Timestamp <= topOffset + 3 && entry.Hashes != null)
                    {
                        foreach (var hash in entry.Hashes)
                        {
                            if (!string.IsNullOrEmpty(hash.Hash))
                            {
                                origHashSet.Add(hash.Hash);
                                origHashList.Add(hash.Hash);
                            }
                        }
                    }
                }
                
                // 교집합 계산
                var intersection = new HashSet<string>(liveHashSet);
                intersection.IntersectWith(origHashSet);
                
                System.Diagnostics.Debug.WriteLine($"\n  [Live 0-3초 vs 원본 {topOffset}-{topOffset+3}초 해시 비교]");
                System.Diagnostics.Debug.WriteLine($"    Live 해시 수 (고유): {liveHashSet.Count}개");
                System.Diagnostics.Debug.WriteLine($"    원본 해시 수 (고유): {origHashSet.Count}개");
                System.Diagnostics.Debug.WriteLine($"    ★ 교집합 (공통 해시): {intersection.Count}개 ({(liveHashSet.Count > 0 ? (100.0 * intersection.Count / liveHashSet.Count) : 0):F1}%)");
                
                // ★★★ 타임스탬프별 해시 개수 분포 분석 ★★★
                System.Diagnostics.Debug.WriteLine($"\n  [타임스탬프별 해시 개수 분포]");
                
                // Live 타임스탬프별 해시 개수
                var liveTimestampDist = new Dictionary<int, int>();
                foreach (var entry in liveFpts)
                {
                    if (entry.Hashes != null)
                    {
                        int ts = entry.Timestamp;
                        if (!liveTimestampDist.ContainsKey(ts)) liveTimestampDist[ts] = 0;
                        liveTimestampDist[ts] += entry.Hashes.Count;
                    }
                }
                System.Diagnostics.Debug.WriteLine($"    [Live 타임스탬프 분포]");
                foreach (var kvp in liveTimestampDist.OrderBy(k => k.Key).Take(10))
                {
                    System.Diagnostics.Debug.WriteLine($"      {kvp.Key}초: {kvp.Value}개 해시");
                }
                System.Diagnostics.Debug.WriteLine($"      ... 총 {liveTimestampDist.Count}개 고유 타임스탬프, 총 해시: {liveTimestampDist.Values.Sum()}개");
                
                // 원본 타임스탬프별 해시 개수 (topOffset 근처)
                var origTimestampDist = new Dictionary<int, int>();
                foreach (var entry in originalFpts)
                {
                    if (entry.Timestamp >= topOffset - 1 && entry.Timestamp <= topOffset + 5 && entry.Hashes != null)
                    {
                        int ts = entry.Timestamp;
                        if (!origTimestampDist.ContainsKey(ts)) origTimestampDist[ts] = 0;
                        origTimestampDist[ts] += entry.Hashes.Count;
                    }
                }
                System.Diagnostics.Debug.WriteLine($"    [원본 {topOffset}초 근처 타임스탬프 분포]");
                foreach (var kvp in origTimestampDist.OrderBy(k => k.Key))
                {
                    System.Diagnostics.Debug.WriteLine($"      {kvp.Key}초: {kvp.Value}개 해시");
                }
                System.Diagnostics.Debug.WriteLine($"      ... 총 {origTimestampDist.Count}개 고유 타임스탬프, 총 해시: {origTimestampDist.Values.Sum()}개");
                
                // 샘플 비교: Live 해시 10개 중 원본에 있는지 확인
                System.Diagnostics.Debug.WriteLine($"\n  [Live 해시 샘플 20개 vs 원본]");
                int matchCount = 0;
                int checkCount = 0;
                foreach (var liveHash in liveHashList.Take(20))
                {
                    checkCount++;
                    bool inOrig = origHashSet.Contains(liveHash);
                    if (inOrig) matchCount++;
                    System.Diagnostics.Debug.WriteLine($"    {liveHash} → {(inOrig ? "✓ 원본에 있음" : "✗ 원본에 없음")}");
                }
                System.Diagnostics.Debug.WriteLine($"  ★ 샘플 {checkCount}개 중 {matchCount}개 매칭 ({(checkCount > 0 ? (100.0 * matchCount / checkCount) : 0):F1}%)");
                
                // 원본 해시 샘플도 표시
                System.Diagnostics.Debug.WriteLine($"\n  [원본 {topOffset}초 해시 샘플 10개]");
                foreach (var origHash in origHashList.Take(10))
                {
                    bool inLive = liveHashSet.Contains(origHash);
                    System.Diagnostics.Debug.WriteLine($"    {origHash} → {(inLive ? "✓ Live에 있음" : "✗ Live에 없음")}");
                }
                
                System.Diagnostics.Debug.WriteLine($"★★★ [해시 직접 비교 진단 끝] ★★★\n");
            }

            return (true, OffsetConcentration);
        }

        /// <summary>
        /// 해시 형식 및 매칭 문제 진단 함수
        /// 원본과 라이브 해시의 형식 차이를 분석합니다.
        /// </summary>
        public static void DiagnoseHashMismatch(
            List<FptEntry> liveFingerprints,
            Dictionary<ulong, List<int>> referenceIndex,
            Action<string> statusCallback = null,
            int actualAudioPositionSeconds = -1,  // 실제 오디오 위치 (초)
            List<FptEntry> originalFingerprints = null)  // 원본 핑거프린트 (선택)
        {
            var sb = new StringBuilder();
            sb.AppendLine("\n=== 해시 매칭 진단 ===");

            // 1. 라이브 핑거프린트 해시 샘플 및 타임스탬프 범위
            sb.AppendLine("\n[라이브 핑거프린트 해시 샘플 (처음 10개)]");
            int liveHashCount = 0;
            int liveMinTs = int.MaxValue;
            int liveMaxTs = int.MinValue;
            var liveHashSamples = new List<string>();
            foreach (var entry in liveFingerprints)
            {
                if (entry.Timestamp < liveMinTs) liveMinTs = entry.Timestamp;
                if (entry.Timestamp > liveMaxTs) liveMaxTs = entry.Timestamp;
                if (entry.Hashes == null) continue;
                foreach (var hash in entry.Hashes)
                {
                    if (string.IsNullOrEmpty(hash.Hash)) continue;
                    liveHashSamples.Add(hash.Hash);
                    liveHashCount++;
                    if (liveHashSamples.Count >= 10) break;
                }
                if (liveHashSamples.Count >= 10) break;
            }
            foreach (var h in liveHashSamples)
            {
                sb.AppendLine($"  {h} (길이:{h.Length})");
            }
            sb.AppendLine($"  ... 총 {liveHashCount}개");
            sb.AppendLine($"\n[라이브 핑거프린트 타임스탬프 범위]");
            sb.AppendLine($"  타임스탬프 범위: {liveMinTs}초 ~ {liveMaxTs}초");

            // 2. 역인덱스 해시 샘플 및 타임스탬프 범위
            sb.AppendLine("\n[역인덱스 해시 샘플 (처음 10개)]");
            int refHashCount = 0;
            int minTimestamp = int.MaxValue;
            int maxTimestamp = int.MinValue;
            foreach (var kvp in referenceIndex)
            {
                if (refHashCount < 10)
                {
                    string hexHash = kvp.Key.ToString("X16");
                    sb.AppendLine($"  {hexHash} (ulong: {kvp.Key}, 타임스탬프 수: {kvp.Value.Count})");
                }
                refHashCount++;
                // 타임스탬프 범위 추적
                foreach (var ts in kvp.Value)
                {
                    if (ts < minTimestamp) minTimestamp = ts;
                    if (ts > maxTimestamp) maxTimestamp = ts;
                }
            }
            sb.AppendLine($"  ... 총 {referenceIndex.Count}개 해시");
            sb.AppendLine($"\n[역인덱스 타임스탬프 범위]");
            sb.AppendLine($"  첫 번째 타임스탬프: {minTimestamp}초");
            sb.AppendLine($"  마지막 타임스탬프: {maxTimestamp}초");
            sb.AppendLine($"  총 시간: {maxTimestamp - minTimestamp}초 ({TimeSpan.FromSeconds(maxTimestamp - minTimestamp):hh\\:mm\\:ss})");
            
            // 라이브 타임스탬프 근처의 역인덱스 해시 찾기
            // 주의: 라이브 핑거프린트의 타임스탬프는 상대적 0초임
            // 실제 오디오 위치가 전달되면 해당 구간을 검색
            int targetTimestamp = actualAudioPositionSeconds >= 0 
                ? actualAudioPositionSeconds 
                : minTimestamp; // 실제 위치가 없으면 역인덱스의 첫 번째 타임스탬프
            sb.AppendLine($"\n[역인덱스에서 {targetTimestamp}초 ~ {targetTimestamp + 1}초 구간의 해시]");
            int foundHashesAtTarget = 0;
            var hashesAtTarget = new List<string>();
            foreach (var kvp in referenceIndex)
            {
                foreach (var ts in kvp.Value)
                {
                    if (ts >= targetTimestamp && ts <= targetTimestamp + 1)
                    {
                        if (hashesAtTarget.Count < 10)
                        {
                            hashesAtTarget.Add($"  {kvp.Key:X16} (ts:{ts}s)");
                        }
                        foundHashesAtTarget++;
                        break; // 같은 해시는 한 번만 카운트
                    }
                }
            }
            if (hashesAtTarget.Count > 0)
            {
                foreach (var h in hashesAtTarget)
                {
                    sb.AppendLine(h);
                }
                sb.AppendLine($"  ... 총 {foundHashesAtTarget}개 해시가 이 구간에 있음");
            }
            else
            {
                sb.AppendLine($"  ❌ 이 구간에 해시가 없습니다!");
            }
            
            // ★ 원본 핑거프린트에서 해당 시간대의 해시 직접 확인 ★
            if (originalFingerprints != null && actualAudioPositionSeconds >= 0)
            {
                sb.AppendLine($"\n[★ 원본 핑거프린트에서 {actualAudioPositionSeconds}초 구간의 해시 ★]");
                var targetEntries = originalFingerprints
                    .Where(e => e.Timestamp >= actualAudioPositionSeconds && e.Timestamp <= actualAudioPositionSeconds + 1)
                    .ToList();
                
                if (targetEntries.Count > 0)
                {
                    int hashCount = 0;
                    int inReverseIndex = 0;
                    int notInReverseIndex = 0;
                    
                    foreach (var entry in targetEntries)
                    {
                        if (entry.Hashes == null) continue;
                        foreach (var hash in entry.Hashes.Take(10))
                        {
                            if (hashCount >= 10) break;
                            
                            // ★ 원본 해시가 역인덱스에 있는지 확인 ★
                            ulong hashValue = FingerprintHashData_mp.HexStringToUlong(hash.Hash);
                            bool existsInReverseIndex = referenceIndex.ContainsKey(hashValue);
                            string status = existsInReverseIndex ? "✓역인덱스有" : "✗역인덱스無";
                            
                            sb.AppendLine($"  [{entry.Timestamp}s] {hash.Hash} ({status}) (F1={hash.Frequency1:F0}, F2={hash.Frequency2:F0}, dt={hash.TimeDelta:F3})");
                            hashCount++;
                            
                            if (existsInReverseIndex) inReverseIndex++;
                            else notInReverseIndex++;
                        }
                        if (hashCount >= 10) break;
                    }
                    int totalHashesInRange = targetEntries.Sum(e => e.Hashes?.Count ?? 0);
                    sb.AppendLine($"  ... 총 {totalHashesInRange}개 해시가 이 구간에 있음");
                    sb.AppendLine($"  ★ 샘플 {hashCount}개 중: 역인덱스에 {inReverseIndex}개 있음, {notInReverseIndex}개 없음 ★");
                }
                else
                {
                    sb.AppendLine($"  ❌ 이 구간에 엔트리가 없습니다!");
                }
            }

            // 3. 라이브 해시를 ulong으로 변환 후 비교
            sb.AppendLine("\n[해시 변환 및 매칭 테스트]");
            int matchCount = 0;
            int convertFailCount = 0;
            int zeroHashCount = 0;
            foreach (var entry in liveFingerprints.Take(10))
            {
                if (entry.Hashes == null) continue;
                foreach (var hash in entry.Hashes.Take(5))
                {
                    if (string.IsNullOrEmpty(hash.Hash)) continue;
                    
                    ulong hashValue = FingerprintHashData_mp.HexStringToUlong(hash.Hash);
                    if (hashValue == 0UL)
                    {
                        zeroHashCount++;
                        sb.AppendLine($"  ❌ 변환 실패: '{hash.Hash}' → 0UL");
                        continue;
                    }
                    
                    bool found = referenceIndex.ContainsKey(hashValue);
                    string status = found ? "✓ 매칭" : "✗ 미매칭";
                    sb.AppendLine($"  {status}: '{hash.Hash}' → {hashValue} ({hashValue:X16})");
                    if (found) matchCount++;
                }
            }

            // 4. 통계 요약
            sb.AppendLine("\n[통계 요약]");
            sb.AppendLine($"  라이브 해시 총 개수: {liveFingerprints.Sum(e => e.Hashes?.Count ?? 0)}");
            sb.AppendLine($"  역인덱스 해시 총 개수: {referenceIndex.Count}");
            sb.AppendLine($"  0UL 변환 실패 수: {zeroHashCount}");
            sb.AppendLine($"  샘플 매칭 수: {matchCount}");

            // 5. 해시 길이 분포 분석
            var lengthDist = new Dictionary<int, int>();
            foreach (var entry in liveFingerprints)
            {
                if (entry.Hashes == null) continue;
                foreach (var hash in entry.Hashes)
                {
                    if (string.IsNullOrEmpty(hash.Hash)) continue;
                    int len = hash.Hash.Length;
                    if (!lengthDist.ContainsKey(len)) lengthDist[len] = 0;
                    lengthDist[len]++;
                }
            }
            sb.AppendLine("\n[라이브 해시 길이 분포]");
            foreach (var kv in lengthDist.OrderBy(x => x.Key))
            {
                sb.AppendLine($"  길이 {kv.Key}: {kv.Value}개");
            }

            statusCallback?.Invoke(sb.ToString());
            System.Diagnostics.Debug.WriteLine(sb.ToString());
        }
        
       /// <summary>
        /// OffsetCluster는 오디오 매칭에서 시간 오프셋들을 묶어 중심을 계산하는 클래스이며, 
        /// 매칭 결과의 신뢰도(Confidence)를 높이고 노이즈에 강건한 매칭을 가능하게 하는 핵심 요소
        /// 매칭 횟수가 많은 오프셋에 더 높은 가중치를 부여하여, 노이즈나 이상치의 영향을 최소화하면서 정확한 매칭 시간을 찾을 수 있습니다.
        /// </summary>
        private sealed class OffsetCluster
        {
            public int CenterOffset { get; set; } /// 클러스터의 중심 오프셋 (가중 평균)
            public double PreciseCenterOffset { get; private set; }  // 소수점 정밀도
            public int TotalMatches { get; set; }  /// 클러스터 내 총 매칭 수
            public double TotalScore { get; set; } /// 클러스터 내 총 점수 (가중치 포함)
            public List<int> Offsets { get; } /// 클러스터에 포함된 오프셋 목록
            private readonly Dictionary<int, int> _offsetCounts;
            /// <summary>
            /// 클러스터의 분산 (낮을수록 집중도 높음)
            /// </summary>
            public double Variance { get; private set; }

            /// <summary>
            /// 클러스터의 신뢰도 (0~1, 높을수록 신뢰)
            /// </summary>
            public double Confidence { get; private set; }

            public OffsetCluster(int initialOffset, int initialCount, double initialScore)
            {
                CenterOffset = initialOffset;
                PreciseCenterOffset = initialOffset;
                TotalMatches = initialCount;
                TotalScore = initialScore;
                Offsets = new List<int> { initialOffset };
                _offsetCounts = new Dictionary<int, int> { { initialOffset, initialCount } };
                Variance = 0;
                Confidence = 1.0;
            }

            public void AddOffset(int offset, int count, double score)
            {
                Offsets.Add(offset);

                if (_offsetCounts.ContainsKey(offset))
                    _offsetCounts[offset] += count;
                else
                    _offsetCounts[offset] = count;

                TotalMatches += count;
                TotalScore += score;

                // 가중 평균 계산
                long weightedSum = 0;
                int totalWeight = 0;

                foreach (var kvp in _offsetCounts)
                {
                    weightedSum += (long)kvp.Key * kvp.Value;
                    totalWeight += kvp.Value;
                }

                if (totalWeight > 0)
                {
                    PreciseCenterOffset = (double)weightedSum / totalWeight;
                    CenterOffset = (int)Math.Round(PreciseCenterOffset);
                }

                // 가중 분산 계산 (집중도 측정)
                double weightedVarianceSum = 0;
                foreach (var kvp in _offsetCounts)
                {
                    double diff = kvp.Key - PreciseCenterOffset;
                    weightedVarianceSum += kvp.Value * diff * diff;
                }
                Variance = totalWeight > 0 ? weightedVarianceSum / totalWeight : 0;

                // 신뢰도 계산 (분산이 낮고 매칭 수가 많을수록 높음)
                // 분산이 1 이하면 매우 집중됨
                double varianceFactor = 1.0 / (1.0 + Variance);
                double countFactor = Math.Min(1.0, TotalMatches / 100.0);  // 100회 이상이면 최대
                Confidence = varianceFactor * 0.7 + countFactor * 0.3;
            }
            public override string ToString()
            {
                return $"Center: {CenterOffset}s (precise: {PreciseCenterOffset:F2}s), " +
                       $"Matches: {TotalMatches}, Variance: {Variance:F2}, " +
                       $"Confidence: {Confidence:P1}";
            }
        }

        

        /// <summary>
        /// 필터링된 역인덱스 생성
        /// 2026.01.21: 과다 출현 해시 필터링 기능 추가
        /// </summary>
        public static Dictionary<ulong, List<int>> BuildFilteredReverseIndex(
            List<FptEntry> fingerprints, Action<string> statusCallback = null, int? maxHashOccurrences = DefaultMaxHashOccurrences)
        {
            if (fingerprints == null || fingerprints.Count == 0)
                return new Dictionary<ulong, List<int>>();

            // 1단계: 전체 역인덱스 생성
            statusCallback?.Invoke("역인덱스 생성 중...");
            var fullIndex = new Dictionary<ulong, List<int>>();
            int totalTimestamps = 0;
            
            // ★ 디버그: 변환 통계 ★
            int totalHashStrings = 0;
            int nullOrEmptyCount = 0;
            int zeroConversionCount = 0;
            int successfulAddCount = 0;
            string sampleHashString = null;
            ulong sampleHashValue = 0;
            
            // ★★★ 2026-02-03: 해시 문자열 길이 분포 분석 ★★★
            var hashLengthCounts = new Dictionary<int, int>();
            var sampleHashStrings32bit = new List<string>(); // 32비트로 변환되는 해시 문자열 샘플

            // ★★★ 2026-02-07: 809초 엔트리 디버깅 ★★★
            bool logged809 = false;
            
            foreach (var entry in fingerprints)
            {
                if (entry == null || entry.Hashes == null) continue;
                totalTimestamps = Math.Max(totalTimestamps, entry.Timestamp);

                foreach (var hash in entry.Hashes)
                {
                    totalHashStrings++;
                    
                    if (hash == null || string.IsNullOrEmpty(hash.Hash)) 
                    {
                        nullOrEmptyCount++;
                        continue;
                    }
                    
                    // ★ 해시 문자열 길이 카운트 ★
                    int hashLen = hash.Hash.Length;
                    if (!hashLengthCounts.ContainsKey(hashLen))
                        hashLengthCounts[hashLen] = 0;
                    hashLengthCounts[hashLen]++;

                    ulong hashValue = FingerprintHashData_mp.HexStringToUlong(hash.Hash);
                    
                    // ★★★ 809초 엔트리 첫 번째 해시 로그 ★★★
                    if (!logged809 && entry.Timestamp == 809)
                    {
                        logged809 = true;
                        statusCallback?.Invoke($"[809초 디버그] 해시 문자열: '{hash.Hash}' (길이:{hashLen}) → ulong: {hashValue} (0x{hashValue:X16})");
                    }
                    
                    // ★ 32비트 해시 문자열 샘플 수집 ★
                    if ((hashValue >> 32) == 0 && hashValue != 0 && sampleHashStrings32bit.Count < 5)
                    {
                        sampleHashStrings32bit.Add(hash.Hash);
                    }
                    
                    // ★ 첫 번째 해시 샘플 저장 ★
                    if (sampleHashString == null && entry.Timestamp == 13)
                    {
                        sampleHashString = hash.Hash;
                        sampleHashValue = hashValue;
                    }
                    
                    if (hashValue == 0UL) 
                    {
                        zeroConversionCount++;
                        continue;
                    }

                    if (!fullIndex.TryGetValue(hashValue, out var timestamps))
                    {
                        timestamps = new List<int>();
                        fullIndex[hashValue] = timestamps;
                    }

                    // 중복 타임스탬프 방지
                    if (timestamps.Count == 0 || timestamps[timestamps.Count - 1] != entry.Timestamp)
                    {
                        timestamps.Add(entry.Timestamp);
                        successfulAddCount++;
                    }
                }
            }
            
            // ★ 디버그 로그 출력 ★
            statusCallback?.Invoke($"[BuildFilteredReverseIndex 통계] 총 해시: {totalHashStrings}, null/empty: {nullOrEmptyCount}, 0변환: {zeroConversionCount}, 성공추가: {successfulAddCount}");
            if (sampleHashString != null)
            {
                statusCallback?.Invoke($"[13초 샘플] 문자열: '{sampleHashString}' (길이:{sampleHashString.Length}) → ulong: {sampleHashValue} (0x{sampleHashValue:X16})");
            }
            
            // ★★★ 2026-02-03: 해시 문자열 길이 분포 출력 ★★★
            var sortedLengths = hashLengthCounts.OrderByDescending(kvp => kvp.Value).ToList();
            statusCallback?.Invoke($"[해시 문자열 길이 분포]");
            foreach (var kvp in sortedLengths.Take(5))
            {
                double pct = (double)kvp.Value / totalHashStrings * 100;
                statusCallback?.Invoke($"  {kvp.Key}자리: {kvp.Value}개 ({pct:F1}%)");
            }
            
            // ★★★ 32비트로 변환되는 해시 문자열 샘플 출력 ★★★
            if (sampleHashStrings32bit.Count > 0)
            {
                statusCallback?.Invoke($"[32비트로 변환되는 해시 문자열 샘플]");
                foreach (var s in sampleHashStrings32bit)
                {
                    ulong val = FingerprintHashData_mp.HexStringToUlong(s);
                    statusCallback?.Invoke($"  문자열: '{s}' → ulong: {val} (0x{val:X16})");
                }
            }
            
            // ★★★ 2026-02-03: 첫 번째 해시 샘플 추가 진단 ★★★
            var firstEntryWithHash = fingerprints.FirstOrDefault(e => e?.Hashes != null && e.Hashes.Count > 0);
            if (firstEntryWithHash?.Hashes?.FirstOrDefault()?.Hash != null)
            {
                var firstHash = firstEntryWithHash.Hashes.First().Hash;
                ulong firstHashValue = FingerprintHashData_mp.HexStringToUlong(firstHash);
                statusCallback?.Invoke($"[첫번째 해시] 문자열: '{firstHash}' (길이:{firstHash.Length}) → ulong: {firstHashValue} (0x{firstHashValue:X16})");
                
                // 해시 길이 경고
                if (firstHash.Length != 16)
                {
                    statusCallback?.Invoke($"⚠️ 경고: 해시 문자열이 16자리가 아닙니다! FPT 파일이 32비트 해시로 저장되어 있을 수 있습니다.");
                }
            }
            
            // ★★★ 2026-02-03: 32비트 vs 64비트 해시 비율 분석 ★★★
            int count32bit = 0;
            int count64bit = 0;
            var sample32bit = new List<ulong>();
            var sample64bit = new List<ulong>();
            
            foreach (var hashValue in fullIndex.Keys)
            {
                if ((hashValue >> 32) == 0)
                {
                    count32bit++; // 상위 32비트가 0 → 32비트 해시
                    if (sample32bit.Count < 5) sample32bit.Add(hashValue);
                }
                else
                {
                    count64bit++; // 상위 32비트가 유효 → 64비트 해시
                    if (sample64bit.Count < 5) sample64bit.Add(hashValue);
                }
            }
            int totalUniqueHashes = count32bit + count64bit;
            double ratio32bit = totalUniqueHashes > 0 ? (double)count32bit / totalUniqueHashes * 100 : 0;
            statusCallback?.Invoke($"[해시 비트 분석] 총 {totalUniqueHashes}개 고유 해시: 64비트={count64bit}개, 32비트(상위0)={count32bit}개 ({ratio32bit:F1}%)");
            
            // ★★★ 샘플 출력 ★★★
            if (sample32bit.Count > 0)
            {
                statusCallback?.Invoke($"  [32비트 해시 샘플]");
                foreach (var h in sample32bit)
                    statusCallback?.Invoke($"    0x{h:X16} (값: {h})");
            }
            if (sample64bit.Count > 0)
            {
                statusCallback?.Invoke($"  [64비트 해시 샘플]");
                foreach (var h in sample64bit)
                    statusCallback?.Invoke($"    0x{h:X16} (값: {h})");
            }
            
            if (ratio32bit > 50)
            {
                statusCallback?.Invoke($"⚠️ 경고: 32비트 해시가 {ratio32bit:F1}% 이상입니다! FPT 재생성을 권장합니다.");
            }

            // 2단계: 과다 출현 해시 분석
            statusCallback?.Invoke("과다 출현 해시 분석 중...");

            int maxAllowedTimestamps;
            if (maxHashOccurrences.HasValue)
            {
                // 매개변수로 전달된 값 우선 사용
                maxAllowedTimestamps = maxHashOccurrences.Value;
            }
            else if (HashFilterConfig.EnableFiltering)
            {
                // HashFilterConfig 설정 사용
                maxAllowedTimestamps = Math.Min(HashFilterConfig.MaxTimestampsPerHash, 
                    (int)(totalTimestamps * HashFilterConfig.MaxTimestampRatio));
            }
            else
            {
                maxAllowedTimestamps = int.MaxValue;
            }

            // 최소 10개는 허용
            maxAllowedTimestamps = Math.Max(maxAllowedTimestamps, 10);

            // 3단계: 필터링 적용
            var filteredIndex = new Dictionary<ulong, List<int>>();
            int filteredCount = 0;
            int keptCount = 0;

            // 통계 수집
            var frequencyDistribution = new Dictionary<int, int>();  // 등장횟수 → 해시수

            foreach (var kvp in fullIndex)
            {
                int occurrenceCount = kvp.Value.Count;

                // 통계 기록
                int bucket = occurrenceCount <= 10 ? occurrenceCount :
                             occurrenceCount <= 50 ? (occurrenceCount / 10) * 10 :
                             occurrenceCount <= 100 ? (occurrenceCount / 25) * 25 :
                             (occurrenceCount / 100) * 100;
                if (!frequencyDistribution.ContainsKey(bucket))
                    frequencyDistribution[bucket] = 0;
                frequencyDistribution[bucket]++;

                // 필터링 결정
                if (occurrenceCount <= maxAllowedTimestamps)
                {
                    filteredIndex[kvp.Key] = kvp.Value;
                    keptCount++;
                }
                else
                {
                    filteredCount++;
                    
                    // ★★★ 2026-02-07: 필터링된 해시 중 809초 포함 여부 확인 ★★★
                    if (kvp.Value.Contains(809))
                    {
                        statusCallback?.Invoke($"⚠️ [필터링됨] 809초 포함 해시: 0x{kvp.Key:X16} (출현 {occurrenceCount}회 > 임계값 {maxAllowedTimestamps}회)");
                    }
                }
            }

            // ★★★ 2026-02-07: 809초 포함 해시 필터링 통계 ★★★
            int filtered809Count = 0;
            int kept809Count = 0;
            ulong sampleFiltered809Hash = 0;
            int sampleFiltered809Occurrences = 0;
            
            foreach (var kvp in fullIndex)
            {
                if (kvp.Value.Contains(809))
                {
                    if (kvp.Value.Count > maxAllowedTimestamps)
                    {
                        filtered809Count++;
                        if (sampleFiltered809Hash == 0)
                        {
                            sampleFiltered809Hash = kvp.Key;
                            sampleFiltered809Occurrences = kvp.Value.Count;
                        }
                    }
                    else
                    {
                        kept809Count++;
                    }
                }
            }
            statusCallback?.Invoke($"[809초 해시 통계] 유지: {kept809Count}개, 필터링: {filtered809Count}개");
            if (sampleFiltered809Hash != 0)
            {
                statusCallback?.Invoke($"  필터링된 샘플: 0x{sampleFiltered809Hash:X16} (출현 {sampleFiltered809Occurrences}회 > 임계값 {maxAllowedTimestamps}회)");
            }
            
            // 4단계: 결과 보고
            statusCallback?.Invoke($"필터링 완료: {keptCount}개 유지, {filteredCount}개 제거 " +
                                   $"(임계값: {maxAllowedTimestamps}회)");

            // 상세 통계 출력
            if (statusCallback != null)
            {
                var sb = new StringBuilder();
                sb.AppendLine("\n[해시 등장 빈도 분포]");
                foreach (var bucket in frequencyDistribution.OrderBy(kv => kv.Key))
                {
                    string label = bucket.Key <= 10 ? $"{bucket.Key}회" : $"{bucket.Key}+회";
                    string bar = new string('█', Math.Min(50, bucket.Value / 100 + 1));
                    sb.AppendLine($"  {label,8}: {bucket.Value,8}개 {bar}");
                }
                statusCallback(sb.ToString());
            }

            return filteredIndex;
        }

        /// <summary>
        /// 개선된 매칭 함수 - 과다 출현 해시 실시간 필터링
        /// MatchFingerprints() 함수 대신 사용 - 2026.01.21
        /// </summary>
        public static FingerprintMatchResult MatchFingerprintsWithFiltering(
            List<FptEntry> liveFingerprints,
            Dictionary<ulong, List<int>> referenceIndex,
            double minConfidence = 0.3,
            int maxHashOccurrences = DefaultMaxHashOccurrences)
        {
            if (liveFingerprints == null || liveFingerprints.Count == 0 ||
                referenceIndex == null || referenceIndex.Count == 0)
            {
                return new FingerprintMatchResult
                {
                    IsMatched = false,
                    Confidence = 0,
                    MatchedTime = TimeSpan.Zero,
                    MatchedHashCount = 0,
                    TotalHashCount = 0
                };
            }

            var offsetHistogram = new Dictionary<int, int>();
            var offsetScores = new Dictionary<int, double>();
            int totalLiveHashes = 0;
            int matchedHashCount = 0;
            int filteredHashCount = 0;  // 필터링된 해시 수

            foreach (var liveEntry in liveFingerprints)
            {
                if (liveEntry.Hashes == null) continue;

                int liveTimestamp = liveEntry.Timestamp;

                foreach (var hash in liveEntry.Hashes)
                {
                    if (string.IsNullOrEmpty(hash.Hash)) continue;

                    totalLiveHashes++;

                    ulong hashValue = FingerprintHashData_mp.HexStringToUlong(hash.Hash);
                    if (hashValue == 0UL) continue;

                    if (referenceIndex.TryGetValue(hashValue, out var refTimestamps))
                    {
                        // ★ 핵심: 과다 출현 해시 필터링 ★
                        if (refTimestamps.Count > maxHashOccurrences)
                        {
                            filteredHashCount++;
                            continue;  // 이 해시는 매칭에서 제외
                        }

                        matchedHashCount++;

                        // IDF 가중치 계산
                        double weight = 1.0 / (1.0 + Math.Log(refTimestamps.Count));

                        foreach (var refTimestamp in refTimestamps)
                        {
                            int offset = refTimestamp - liveTimestamp;

                            if (!offsetHistogram.ContainsKey(offset))
                                offsetHistogram[offset] = 0;
                            offsetHistogram[offset]++;

                            if (!offsetScores.ContainsKey(offset))
                                offsetScores[offset] = 0;
                            offsetScores[offset] += weight;
                        }
                    }
                }
            }

            if (offsetHistogram.Count == 0 || totalLiveHashes == 0)
            {
                return new FingerprintMatchResult
                {
                    IsMatched = false,
                    Confidence = 0,
                    MatchedTime = TimeSpan.Zero,
                    MatchedHashCount = 0,
                    TotalHashCount = totalLiveHashes
                };
            }

            // 오프셋 클러스터링
            var clusters = ClusterOffsetsImproved(offsetScores, offsetHistogram, toleranceSeconds: 1);

            var bestCluster = clusters
                .OrderByDescending(c => c.TotalMatches)
                .FirstOrDefault();

            if (bestCluster == null || bestCluster.TotalMatches == 0)
            {
                return new FingerprintMatchResult
                {
                    IsMatched = false,
                    Confidence = 0,
                    MatchedTime = TimeSpan.Zero,
                    MatchedHashCount = matchedHashCount,
                    TotalHashCount = totalLiveHashes
                };
            }

            // 신뢰도 계산 (필터링 효과 반영)
            int effectiveHashes = totalLiveHashes - filteredHashCount;
            double confidence1 = effectiveHashes > 0
                ? (double)bestCluster.TotalMatches / effectiveHashes
                : 0;

            double confidence2 = matchedHashCount > 0
                ? (double)bestCluster.TotalMatches / matchedHashCount
                : 0;

            // 클러스터 분리도
            double separationConfidence = 0;
            if (clusters.Count >= 2)
            {
                var sortedClusters = clusters.OrderByDescending(c => c.TotalMatches).ToList();
                separationConfidence = 1.0 - ((double)sortedClusters[1].TotalMatches / sortedClusters[0].TotalMatches);
            }
            else if (clusters.Count == 1)
            {
                separationConfidence = 1.0;
            }

            double finalConfidence =
                confidence1 * 0.3 +
                confidence2 * 0.3 +
                separationConfidence * 0.4;  // 분리도 가중치 증가

            int matchedOriginalTimestamp = bestCluster.CenterOffset;
            bool isMatched = finalConfidence >= minConfidence && bestCluster.TotalMatches >= 3;

            return new FingerprintMatchResult
            {
                IsMatched = isMatched,
                Confidence = finalConfidence,
                MatchedTime = TimeSpan.FromSeconds(Math.Max(0, matchedOriginalTimestamp)),
                MatchedHashCount = bestCluster.TotalMatches,
                TotalHashCount = totalLiveHashes
            };
        }

        /// <summary>
        /// 개선된 오프셋 클러스터링 (가중 평균 적용)
        /// ClusterOffsets() 함수 대신 사용 - 2026.01.21
        /// </summary>
        private static List<OffsetCluster> ClusterOffsetsImproved(
            Dictionary<int, double> offsetScores,
            Dictionary<int, int> offsetCounts,
            int toleranceSeconds)
        {
            var clusters = new List<OffsetCluster>();
            // 점수 기반 정렬
            var sortedOffsets = offsetScores.OrderByDescending(kv => kv.Value).ToList();

            foreach (var kvp in sortedOffsets)
            {
                int offset = kvp.Key;
                double score = kvp.Value;
                // ★★★ 수정: 실제 매칭 횟수 사용 (기존: count=1 하드코딩) ★★★
                int count = offsetCounts.ContainsKey(offset) ? offsetCounts[offset] : 0;
                
                // 기존 클러스터에 추가 가능한지 확인
                OffsetCluster targetCluster = null;
                foreach (var cluster in clusters)
                {
                    if (Math.Abs(offset - cluster.CenterOffset) <= toleranceSeconds)
                    {
                        targetCluster = cluster;
                        break;
                    }
                }

                if (targetCluster != null)
                {
                    // ★★★ 수정: 실제 count 전달 (기존: 1 하드코딩) ★★★
                    targetCluster.AddOffset(offset, count, score);
                }
                else
                {
                    // ★★★ 수정: 실제 count 전달 (기존: 1 하드코딩) ★★★
                    clusters.Add(new OffsetCluster(offset, count, score));
                }
            }

            return clusters;
        }
        /// <summary>
        /// 라이브 핑거프린트와 기준 핑거프린트를 매칭합니다. (기존 방식 - 호환성)
        /// 역인덱스가 있으면 자동으로 사용합니다.
        /// </summary>
        /// <param name="liveFingerprints">라이브 오디오에서 생성된 핑거프린트</param>
        /// <param name="referenceFingerprints">기준 오디오의 핑거프린트</param>
        /// <param name="minConfidence">최소 신뢰도 임계값 (기본값: 0.7)</param>
        /// <param name="reverseIndex">기준 오디오의 역인덱스 (선택적, 있으면 사용)</param>
        /// <returns>매칭 결과</returns>
        public static FingerprintMatchResult MatchFingerprints(
            List<FptEntry> liveFpts, 
            List<FptEntry> referenceFpts = null,
            Dictionary<ulong, List<int>> reverseIndex = null,
            double minConfidence = 0.3, int? maxHashOccurrences = DefaultMaxHashOccurrences)
        {
            // Step 1: 역인덱스 준비
            if (reverseIndex == null)
            {
                if (referenceFpts == null)
                    throw new ArgumentException("역인덱스 또는 기준 핑거프린트 필요");

                reverseIndex = BuildFilteredReverseIndex(referenceFpts, null, maxHashOccurrences);
            }

            // Step 2: 매칭 수행 (모든 최적화 적용)
            // 가중치 적용 히스토그램 (점수 기반)
            var offsetScores = new Dictionary<int, double>();
            var offsetCounts = new Dictionary<int, int>();
            int totalHashes = 0;
            int matchedHashes = 0;

            foreach (var liveEntry in liveFpts)
            {
                if (liveEntry.Hashes == null) continue;

                foreach (var hash in liveEntry.Hashes)
                {
                    if (string.IsNullOrEmpty(hash.Hash)) continue;

                    totalHashes++;
                    ulong hashValue = FingerprintHashData_mp.HexStringToUlong(hash.Hash);

                    // 역인덱스 직접 조회 (O(1))
                    if (reverseIndex.TryGetValue(hashValue, out var refTimestamps))
                    {
                        // 필터링 적용 (선택적)
                        if (maxHashOccurrences.HasValue &&
                            refTimestamps.Count > maxHashOccurrences.Value)
                        {
                            continue;
                        }

                        matchedHashes++;

                        // ★ 가중치 적용 (IDF: Inverse Document Frequency) ★
                        // 흔한 해시(refTimestamps.Count가 큼)는 낮은 점수, 희귀 해시는 높은 점수
                        double weight = 1.0 / (1.0 + Math.Log(refTimestamps.Count));

                        foreach (var refTs in refTimestamps)
                        {
                            int offset = refTs - liveEntry.Timestamp;

                            if (!offsetScores.ContainsKey(offset))
                                offsetScores[offset] = 0;
                            offsetScores[offset] += weight;

                            if (!offsetCounts.ContainsKey(offset))
                                offsetCounts[offset] = 0;
                            offsetCounts[offset]++;
                        }
                    }
                }
            }

            // Step 3: 다층 신뢰도 계산
            if (offsetScores.Count == 0) return CreateNoMatchResult(totalHashes);

            // 3-1. 오프셋 클러스터링
            var clusters = ClusterOffsetsImproved(offsetScores, offsetCounts, 1);
            // 점수(Score) 기반으로 정렬하여 베스트 클러스터 선택
            var bestCluster = clusters.OrderByDescending(c => c.TotalScore).First();

            // 3-2. 신뢰도 계산 (기본)
            double confidence1 = (double)bestCluster.TotalMatches / totalHashes;
            double confidence2 = (matchedHashes > 0) ? (double)bestCluster.TotalMatches / matchedHashes : 0;

            // 3-3. 연속성 검증
            double continuityWeight = Cal_ContinuityWeight(offsetCounts);

            // 3-4. 슬라이딩 윈도우 검증
            var slidingResult = Cal_SlidingWindowMatch(offsetCounts, totalHashes, matchedHashes);

            // 3-5. 해시 시퀀스 검증
            var sequenceResult = Cal_HashSequenceMatchWithIndex(liveFpts, reverseIndex);
            
            // 3-6. RANSAC 기하학적 검증
            var geometricResult = ImprovedMatching.MatchWithGeometricVerification(
                liveFpts, reverseIndex, minConfidence: 0.1);
            double geometricConfidence = geometricResult.Confidence;

            // Step 4: 최종 신뢰도 종합
            // 오프셋 집중도가 낮아도(노이즈가 많아도) 클러스터 자체가 견고하면(분산 낮고 수량 많음) 매칭 인정
            double clusterQuality = bestCluster.Confidence;

            double finalConfidence =
                confidence1 * 0.15 +
                confidence2 * 0.15 +
                (0.7 + continuityWeight * 0.3) * 0.15 +
                Math.Min(sequenceResult.Confidence, 1.0) * 0.15 +
                geometricConfidence * 0.15 +
                clusterQuality * 0.25; // 클러스터 자체 품질에 가중치 부여 (25%)

            // 보정: 클러스터 품질이 매우 높으면 최종 점수 부스팅 (Ratio가 낮아도 매칭 인정)
            if (clusterQuality > 0.8) finalConfidence = Math.Max(finalConfidence, clusterQuality * 0.9);

            // Step 5: 최적 결과 선택
            int bestTimestamp = bestCluster.CenterOffset;
            if (slidingResult.Confidence > finalConfidence * 0.9)
                bestTimestamp = slidingResult.CenterTimestamp;

            return new FingerprintMatchResult
            {
                IsMatched = finalConfidence >= minConfidence && bestCluster.TotalMatches >= 3,
                Confidence = Math.Min(finalConfidence, 1.0),
                MatchedTime = TimeSpan.FromSeconds(Math.Max(0, bestTimestamp)),
                MatchedHashCount = bestCluster.TotalMatches,
                TotalHashCount = totalHashes
            };
        }
        private static FingerprintMatchResult CreateNoMatchResult(int totalHashes)
        {
            return new FingerprintMatchResult
            {
                IsMatched = false,
                Confidence = 0,
                MatchedTime = TimeSpan.Zero,
                MatchedHashCount = 0,
                TotalHashCount = totalHashes
            };
        }
        /// <summary>
        /// 필터링 효과 검증 및 비교 - 2026.01.21 
        /// </summary>
        public static void CompareFilteringEffect(List<FptEntry> liveFpts, Dictionary<ulong, List<int>> referenceIndex)
        {
            Console.WriteLine("=== 과다 출현 해시 필터링 효과 비교 ===\n");

            // 필터링 없이 매칭
            Console.WriteLine("[필터링 없음]");
            var resultNoFilter = SFPFM.MatchFingerprintsWithFiltering(liveFpts, referenceIndex, minConfidence: 0.1, maxHashOccurrences: int.MaxValue);  // 필터링 없음

            var diagNoFilter = DiagnoseMatchInternal(liveFpts, referenceIndex, int.MaxValue);

            Console.WriteLine($"  매칭 결과: {(resultNoFilter.IsMatched ? "성공" : "실패")}");
            Console.WriteLine($"  신뢰도: {resultNoFilter.Confidence:P2}");
            Console.WriteLine($"  매칭 시간: {resultNoFilter.MatchedTime}");
            Console.WriteLine($"  오프셋 집중도: {diagNoFilter.OffsetConcentration:P2}");
            Console.WriteLine($"  상위 오프셋: {string.Join(", ", diagNoFilter.TopOffsets.Take(3).Select(x => $"{x.Offset}초({x.Count}회)"))}");

            // 다양한 필터링 임계값으로 테스트
            int[] thresholds = { 100, 50, 30, 20, 10 };

            foreach (int threshold in thresholds)
            {
                Console.WriteLine($"\n[필터링: {threshold}회 초과 제외]");

                var result = SFPFM.MatchFingerprintsWithFiltering(liveFpts, referenceIndex, minConfidence: 0.1, maxHashOccurrences: threshold);

                var diag = DiagnoseMatchInternal(liveFpts, referenceIndex, threshold);

                Console.WriteLine($"  매칭 결과: {(result.IsMatched ? "성공" : "실패")}");
                Console.WriteLine($"  신뢰도: {result.Confidence:P2}");
                Console.WriteLine($"  매칭 시간: {result.MatchedTime}");
                Console.WriteLine($"  오프셋 집중도: {diag.OffsetConcentration:P2}");
                Console.WriteLine($"  사용된 해시: {diag.UsedHashes}/{diag.TotalHashes} ({(double)diag.UsedHashes / diag.TotalHashes:P1})");
                Console.WriteLine($"  상위 오프셋: {string.Join(", ", diag.TopOffsets.Take(3).Select(x => $"{x.Offset}초({x.Count}회)"))}");

                // 집중도 향상 여부 표시
                double improvement = diag.OffsetConcentration - diagNoFilter.OffsetConcentration;
                if (improvement > 0)
                {
                    Console.WriteLine($"  ✅ 집중도 향상: +{improvement:P2}");
                }
                else
                {
                    Console.WriteLine($"  ⚠️ 집중도 변화: {improvement:P2}");
                }
            }

            Console.WriteLine("\n[권장 설정]");
            Console.WriteLine("  → maxHashOccurrences = 30~50 권장");
            Console.WriteLine("  → 너무 낮으면 유효한 해시도 제거됨");
            Console.WriteLine("  → 너무 높으면 반복 패턴 필터링 효과 감소");
        }

        /// <summary>
        /// 내부 진단 (필터링 적용 상태로)
        /// </summary>
        private static (double OffsetConcentration, List<(int Offset, int Count)> TopOffsets, int UsedHashes, int TotalHashes)
            DiagnoseMatchInternal(List<FptEntry> liveFingerprints, Dictionary<ulong, List<int>> referenceIndex, int maxHashOccurrences)
        {
            var offsetHistogram = new Dictionary<int, int>();
            int totalHashes = 0;
            int usedHashes = 0;
            int totalOccurrences = 0;

            foreach (var entry in liveFingerprints)
            {
                if (entry.Hashes == null) continue;

                foreach (var hash in entry.Hashes)
                {
                    if (string.IsNullOrEmpty(hash.Hash)) continue;

                    totalHashes++;
                    ulong hashValue = FingerprintHashData_mp.HexStringToUlong(hash.Hash);

                    if (referenceIndex.TryGetValue(hashValue, out var refTimestamps))
                    {
                        // 필터링 적용
                        if (refTimestamps.Count > maxHashOccurrences)
                            continue;

                        usedHashes++;

                        foreach (var refTs in refTimestamps)
                        {
                            int offset = refTs - entry.Timestamp;
                            if (!offsetHistogram.ContainsKey(offset))
                                offsetHistogram[offset] = 0;
                            offsetHistogram[offset]++;
                            totalOccurrences++;
                        }
                    }
                }
            }

            var topOffsets = offsetHistogram
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();

            double concentration = topOffsets.Count > 0 && totalOccurrences > 0
                ? (double)topOffsets[0].Value / totalOccurrences
                : 0;

            return (concentration, topOffsets, usedHashes, totalHashes);
        }
        
        /// <summary>
        /// 연속성 가중치 계산 (인접 타임스탬프 매칭 확인)
        /// </summary>
        /// <param name="timestampMatches">타임스탬프별 매칭 수</param>
        /// <returns>연속성 가중치 (0.0 ~ 1.0)</returns>
        private static double Cal_ContinuityWeight(Dictionary<int, int> timestampMatches)
        {
            if (timestampMatches.Count < 2)
            {
                return 0;
            }
            
            var sortedTimestamps = timestampMatches.Keys.OrderBy(t => t).ToList();
            int consecutiveCount = 0;
            int maxConsecutive = 0;
            int totalConsecutivePairs = 0;
            
            for (int i = 1; i < sortedTimestamps.Count; i++)
            {
                int diff = sortedTimestamps[i] - sortedTimestamps[i - 1];
                if (diff <= 2) // 2초 이내면 연속으로 간주
                {
                    consecutiveCount++;
                    maxConsecutive = Math.Max(maxConsecutive, consecutiveCount);
                    totalConsecutivePairs++;
                }
                else
                {
                    consecutiveCount = 0;
                }
            }
            
            // 연속성 비율 계산 (연속 쌍 수 / 전체 쌍 수)
            double continuityRatio = sortedTimestamps.Count > 1 
                ? (double)totalConsecutivePairs / (sortedTimestamps.Count - 1) 
                : 0;
            
            // 최대 연속 길이 비율
            double maxConsecutiveRatio = sortedTimestamps.Count > 1 
                ? (double)maxConsecutive / sortedTimestamps.Count 
                : 0;
            
            // 두 비율의 평균을 연속성 가중치로 사용
            return (continuityRatio + maxConsecutiveRatio) / 2.0;
        }
        
        /// <summary>
        /// 슬라이딩 윈도우 매칭 결과
        /// </summary>
        private sealed class SlidingWindowResult
        {
            public int CenterTimestamp { get; set; }
            public int MatchCount { get; set; }
            public double Confidence { get; set; }
            public int StartTime { get; set; }
            public int EndTime { get; set; }
        }
        
        /// <summary>
        /// 슬라이딩 윈도우로 최적 매칭 구간을 찾습니다.
        /// </summary>
        /// <param name="timestampMatches">타임스탬프별 매칭 수</param>
        /// <param name="totalHashes">전체 해시 수</param>
        /// <param name="matchedHashes">매칭된 해시 수</param>
        /// <returns>슬라이딩 윈도우 매칭 결과</returns>
        private static SlidingWindowResult Cal_SlidingWindowMatch(
            Dictionary<int, int> timestampMatches,
            int totalHashes,
            int matchedHashes)
        {
            if (timestampMatches.Count == 0 || totalHashes == 0)
            {
                return new SlidingWindowResult
                {
                    CenterTimestamp = 0,
                    MatchCount = 0,
                    Confidence = 0,
                    StartTime = 0,
                    EndTime = 0
                };
            }
            
            // 타임스탬프 범위 확인
            int minTimestamp = timestampMatches.Keys.Min();
            int maxTimestamp = timestampMatches.Keys.Max();
            
            // 윈도우 크기와 스텝 크기 설정
            int windowSize = 5; // 5초 윈도우
            int stepSize = 1; // 1초씩 이동
            
            // 최소 윈도우 크기보다 작으면 전체 범위 사용
            if (maxTimestamp - minTimestamp < windowSize)
            {
                windowSize = maxTimestamp - minTimestamp + 1;
                if (windowSize <= 0)
                {
                    windowSize = 1;
                }
            }
            
            var windowScores = new List<SlidingWindowResult>();
            
            // 슬라이딩 윈도우로 매칭 구간 찾기
            for (int startTime = minTimestamp; startTime <= maxTimestamp - windowSize + 1; startTime += stepSize)
            {
                int endTime = startTime + windowSize - 1;
                int windowMatches = 0;
                
                // 윈도우 내의 매칭 수 계산
                foreach (var kvp in timestampMatches)
                {
                    if (kvp.Key >= startTime && kvp.Key <= endTime)
                    {
                        windowMatches += kvp.Value;
                    }
                }
                
                // 윈도우 신뢰도 계산
                double windowConfidence = totalHashes > 0 
                    ? (double)windowMatches / totalHashes 
                    : 0;
                
                // 윈도우 중심 타임스탬프 계산
                int centerTimestamp = (startTime + endTime) / 2;
                
                windowScores.Add(new SlidingWindowResult
                {
                    StartTime = startTime,
                    EndTime = endTime,
                    CenterTimestamp = centerTimestamp,
                    MatchCount = windowMatches,
                    Confidence = windowConfidence
                });
            }
            
            // 최고 점수 윈도우 선택 (신뢰도가 같으면 매칭 수가 많은 것)
            var bestWindow = windowScores
                .OrderByDescending(w => w.Confidence)
                .ThenByDescending(w => w.MatchCount)
                .FirstOrDefault();
            
            return bestWindow ?? new SlidingWindowResult
            {
                CenterTimestamp = 0,
                MatchCount = 0,
                Confidence = 0,
                StartTime = 0,
                EndTime = 0
            };
        }

        /// <summary>
        /// 역인덱스를 사용한 해시 시퀀스 매칭 (고성능)
        /// </summary>
        private static HashSequenceMatchResult Cal_HashSequenceMatchWithIndex(
            List<FptEntry> liveFingerprints, Dictionary<ulong, List<int>> referenceHashToTimestamps)
        {
            if (liveFingerprints == null || liveFingerprints.Count < 2 ||
                referenceHashToTimestamps == null || referenceHashToTimestamps.Count == 0)
            {
                return new HashSequenceMatchResult
                {
                    CenterTimestamp = 0,
                    MatchCount = 0,
                    Confidence = 0,
                    TotalSequences = 0
                };
            }

            // 해시 시퀀스 생성 (인접한 두 타임스탬프의 해시 조합)
            // ★ 성능 개선: 시퀀스 수 제한 및 샘플링 ★
            const int MaxHashesPerEntry = 20;  // 각 엔트리에서 최대 해시 수
            const int MaxSequences = 5000;     // 최대 시퀀스 수
            
            var hashSequences = new List<HashSequence>();
            for (int i = 0; i < liveFingerprints.Count - 1; i++)
            {
                var entry1 = liveFingerprints[i];
                var entry2 = liveFingerprints[i + 1];

                if (entry1.Hashes == null || entry1.Hashes.Count == 0 ||
                    entry2.Hashes == null || entry2.Hashes.Count == 0)
                {
                    continue;
                }

                // 각 엔트리에서 해시 수 제한 (샘플링)
                var hashes1 = entry1.Hashes.Count <= MaxHashesPerEntry 
                    ? entry1.Hashes 
                    : entry1.Hashes.Take(MaxHashesPerEntry).ToList();
                var hashes2 = entry2.Hashes.Count <= MaxHashesPerEntry 
                    ? entry2.Hashes 
                    : entry2.Hashes.Take(MaxHashesPerEntry).ToList();

                int timeDelta = entry2.Timestamp - entry1.Timestamp;

                // 각 해시 쌍에 대해 시퀀스 생성
                foreach (var hash1 in hashes1)
                {
                    if (string.IsNullOrEmpty(hash1.Hash)) continue;

                    foreach (var hash2 in hashes2)
                    {
                        if (string.IsNullOrEmpty(hash2.Hash)) continue;

                        hashSequences.Add(new HashSequence
                        {
                            Hash1 = hash1.Hash,
                            Hash2 = hash2.Hash,
                            TimeDelta = timeDelta,
                            LiveTimestamp1 = entry1.Timestamp
                        });

                        // 최대 시퀀스 수 제한
                        if (hashSequences.Count >= MaxSequences)
                        {
                            break;
                        }
                    }
                    if (hashSequences.Count >= MaxSequences) break;
                }
                if (hashSequences.Count >= MaxSequences) break;
            }

            if (hashSequences.Count == 0)
            {
                return new HashSequenceMatchResult
                {
                    CenterTimestamp = 0,
                    MatchCount = 0,
                    Confidence = 0,
                    TotalSequences = 0
                };
            }

            // 역인덱스에서 시퀀스 매칭
            // ★ 성능 개선: HashSet 캐시로 중복 생성 방지 ★
            int sequenceMatches = 0;
            Dictionary<int, int> sequenceTimestampMatches = new Dictionary<int, int>(); // timestamp -> 매칭된 시퀀스 수
            Dictionary<ulong, HashSet<int>> timestampSetCache = new Dictionary<ulong, HashSet<int>>(); // HashSet 캐시

            foreach (var seq in hashSequences)
            {
                // 역인덱스에서 Hash1과 Hash2 조회 (O(1))
                ulong hash1Value = FingerprintHashData_mp.HexStringToUlong(seq.Hash1);
                ulong hash2Value = FingerprintHashData_mp.HexStringToUlong(seq.Hash2);

                if (hash1Value == 0UL || hash2Value == 0UL) continue;

                if (!referenceHashToTimestamps.TryGetValue(hash1Value, out var timestamps1) ||
                    !referenceHashToTimestamps.TryGetValue(hash2Value, out var timestamps2))
                {
                    continue;
                }

                if (timestamps1 == null || timestamps2 == null) continue;

                // ★ 성능 개선: HashSet 캐시 사용 ★
                if (!timestampSetCache.TryGetValue(hash2Value, out var timestamps2Set))
                {
                    timestamps2Set = new HashSet<int>(timestamps2);
                    timestampSetCache[hash2Value] = timestamps2Set;
                }
                
                // timestamps1 조회도 제한 (최대 100개)
                var ts1Limited = timestamps1.Count > 100 ? timestamps1.Take(100) : timestamps1;
                
                foreach (var ts1 in ts1Limited)
                {
                    int expectedTs2 = ts1 + seq.TimeDelta;

                    // Hash2가 예상 타임스탬프에 있는지 확인 (±1초 허용)
                    // O(1) 조회로 개선
                    bool found = timestamps2Set.Contains(expectedTs2) ||
                                 timestamps2Set.Contains(expectedTs2 - 1) ||
                                 timestamps2Set.Contains(expectedTs2 + 1);

                    if (found)
                    {
                        sequenceMatches++;
                        // 시퀀스 매칭 타임스탬프 기록
                        int matchTimestamp = ts1;
                        if (!sequenceTimestampMatches.ContainsKey(matchTimestamp))
                        {
                            sequenceTimestampMatches[matchTimestamp] = 0;
                        }
                        sequenceTimestampMatches[matchTimestamp]++;
                    }
                }
            }

            // 시퀀스 신뢰도 계산
            double sequenceConfidence = hashSequences.Count > 0
                ? (double)sequenceMatches / hashSequences.Count
                : 0;

            // 가장 많이 매칭된 타임스탬프 찾기
            int centerTimestamp = 0;
            int maxSequenceMatches = 0;
            if (sequenceTimestampMatches.Count > 0)
            {
                foreach (var kvp in sequenceTimestampMatches)
                {
                    if (kvp.Value > maxSequenceMatches)
                    {
                        maxSequenceMatches = kvp.Value;
                        centerTimestamp = kvp.Key;
                    }
                }
            }

            return new HashSequenceMatchResult
            {
                CenterTimestamp = centerTimestamp,
                MatchCount = maxSequenceMatches,
                Confidence = sequenceConfidence,
                TotalSequences = hashSequences.Count
            };
        }

        /// <summary>
        /// 해시 시퀀스 (인접 해시 조합)
        /// </summary>
        private sealed class HashSequence
        {
            public string Hash1 { get; set; }
            public string Hash2 { get; set; }
            public int TimeDelta { get; set; } // 두 해시 간 시간 차이 (초)
            public int LiveTimestamp1 { get; set; } // 라이브 핑거프린트의 첫 번째 타임스탬프
        }
        
        /// <summary>
        /// 해시 시퀀스 매칭 결과
        /// </summary>
        private sealed class HashSequenceMatchResult
        {
            public int CenterTimestamp { get; set; }
            public int MatchCount { get; set; }
            public double Confidence { get; set; }
            public int TotalSequences { get; set; }
        }
        
                
        /// <summary>
        /// 타임스탬프 클러스터 (시간 연속성 기반 매칭 그룹)
        /// </summary>
        private sealed class TimestampCluster
        {
            public int CenterTimestamp { get; private set; }
            public int TotalMatches { get; private set; }
            public List<int> Timestamps { get; }
            
            public TimestampCluster(int initialTimestamp, int initialMatches)
            {
                CenterTimestamp = initialTimestamp;
                TotalMatches = initialMatches;
                Timestamps = new List<int> { initialTimestamp };
            }
            
            public void AddMatch(int timestamp, int matchCount)
            {
                Timestamps.Add(timestamp);
                TotalMatches += matchCount;
                // 중심 타임스탬프 재계산 (가중 평균)
                CenterTimestamp = (int)Math.Round(Timestamps.Average());
            }
        }

        /// <summary>
        /// WAV 파일 헤더를 파싱합니다.
        /// </summary>
        private static WaveFileContext ParseWaveHeader(string path)
        {
            using (var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false))
            {
                string riff = Encoding.ASCII.GetString(reader.ReadBytes(4));
                if (!string.Equals(riff, "RIFF", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException("유효한 WAV 파일이 아닙니다 (RIFF 헤더가 없습니다).");
                }

                reader.ReadInt32(); // chunk size

                string wave = Encoding.ASCII.GetString(reader.ReadBytes(4));
                if (!string.Equals(wave, "WAVE", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException("유효한 WAV 파일이 아닙니다 (WAVE 헤더가 없습니다).");
                }

                short audioFormat = 0;
                short channels = 0;
                int sampleRate = 0;
                short bitsPerSample = 0;
                long dataLength = 0;
                long dataStartPosition = 0;
                bool fmtFound = false;
                bool dataFound = false;

                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    string chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
                    int chunkSize = reader.ReadInt32();
                    long nextChunkPosition = reader.BaseStream.Position + chunkSize;

                    switch (chunkId)
                    {
                        case "fmt ":
                            audioFormat = reader.ReadInt16();
                            channels = reader.ReadInt16();
                            sampleRate = reader.ReadInt32();
                            reader.ReadInt32(); // byteRate
                            reader.ReadInt16(); // blockAlign
                            bitsPerSample = reader.ReadInt16();
                            if (chunkSize > 16)
                            {
                                reader.BaseStream.Position += (chunkSize - 16);
                            }
                            fmtFound = true;
                            break;

                        case "data":
                            dataLength = chunkSize;
                            dataStartPosition = reader.BaseStream.Position;
                            reader.BaseStream.Position = nextChunkPosition;
                            dataFound = true;
                            break;

                        default:
                            reader.BaseStream.Position = nextChunkPosition;
                            break;
                    }

                    if ((chunkSize & 1) == 1)
                    {
                        reader.BaseStream.Position++;
                    }
                }

                if (!fmtFound || !dataFound)
                {
                    throw new InvalidOperationException("WAV 파일에서 필수 chunk(fmt, data)를 찾을 수 없습니다.");
                }

                long totalSamples = dataLength / (channels * (bitsPerSample / 8));
                TimeSpan duration = TimeSpan.FromSeconds(totalSamples / (double)sampleRate);

                return new WaveFileContext
                {
                    AudioFormat = audioFormat,
                    Channels = channels,
                    SampleRate = sampleRate,
                    BitsPerSample = bitsPerSample,
                    DataLength = dataLength,
                    DataStartPosition = dataStartPosition,
                    TotalSamples = totalSamples,
                    Duration = duration
                };
            }
        }

        /// <summary>
        /// 청크 단위로 오디오 샘플을 읽습니다. (65536 샘플씩 버퍼링)
        /// </summary>
        /// <param name="reader">BinaryReader</param>
        /// <param name="audioFormat">오디오 포맷</param>
        /// <param name="bitsPerSample">샘플당 비트 수</param>
        /// <param name="channels">채널 수</param>
        /// <param name="chunkSizeMono">청크 크기 (모노 샘플 수)</param>
        /// <param name="cancellationToken">취소 토큰</param>
        /// <returns>모노 샘플 배열</returns>
        private static double[] ReadChunkSamples(BinaryReader reader, short audioFormat, short bitsPerSample, int channels, int chunkSizeMono, CancellationToken cancellationToken)
        {
            int samplesToRead = chunkSizeMono * channels; // 모든 채널의 샘플 수
            var chunk = new List<double>(samplesToRead);
            int bytesPerSample = bitsPerSample / 8;

            try
            {
                // 루프 시작 전 취소 체크 (정식 취소 요청 처리)
                if (cancellationToken != null && cancellationToken.IsCancellationRequested)
                {
                    // 취소 요청 시 메모리 정리 후 null 반환
                    chunk.Clear();
                    return null;
                }
                
                for (int i = 0; i < samplesToRead; i++)
                {
                    // 파일 끝 체크: 필요한 바이트 수 확인
                    long remainingBytes = reader.BaseStream.Length - reader.BaseStream.Position;
                    if (remainingBytes < bytesPerSample)
                    {
                        break; // 더 이상 읽을 샘플이 없음
                    }

                    double sample = 0;

                    try
                    {
                        switch (bitsPerSample)
                        {
                            case 8:
                                sample = (reader.ReadByte() - 128) / 128.0;
                                break;
                            case 16:
                                sample = reader.ReadInt16() / 32768.0;
                                break;
                            case 24:
                                byte[] bytes24 = reader.ReadBytes(3);
                                if (bytes24.Length < 3)
                                {
                                    // 파일 끝에 도달 - 나머지는 0으로 채움
                                    byte[] padded = new byte[3];
                                    Array.Copy(bytes24, padded, bytes24.Length);
                                    bytes24 = padded;
                                }
                                int sample24 = (bytes24[0] | (bytes24[1] << 8) | (bytes24[2] << 16));
                                if ((sample24 & 0x800000) != 0)
                                {
                                    sample24 |= unchecked((int)0xFF000000);
                                }
                                sample = sample24 / 8388608.0;
                                break;
                            case 32:
                                if (audioFormat == 1) // PCM
                                {
                                    sample = reader.ReadInt32() / 2147483648.0;
                                }
                                else // IEEE float
                                {
                                    sample = reader.ReadSingle();
                                }
                                break;
                        }

                        chunk.Add(sample);
                    }
                    catch (EndOfStreamException)
                    {
                        // 파일 끝에 도달 - 루프 종료
                        break;
                    }
                    
                    // 취소 가능한 시점: 샘플 읽기 및 추가 완료 후 (논리적 중단점)
                    // 파일 I/O 작업 사이의 안전한 지점에서만 체크
                    // 정식 취소 요청: 예외를 throw하지 않고 루프 종료
                    if (cancellationToken != null && cancellationToken.IsCancellationRequested && (i + 1) % 1000 == 0)
                    {
                        // 취소 요청 시 루프 중단 (예외 없이)
                        break;
                    }
                }
            }
            catch (EndOfStreamException)
            {
                // EndOfStreamException은 정상적인 파일 끝 처리이므로 그대로 전파
                throw;
            }
            
            // 루프 종료 후 취소 상태 확인 (정식 취소 요청 처리)
            if (cancellationToken != null && cancellationToken.IsCancellationRequested)
            {
                // 취소 요청 시 메모리 정리 후 null 반환
                chunk.Clear();
                return null;
            }

            // 스테레오인 경우 모노로 변환 (평균)
            if (channels == 2)
            {
                // 스테레오 변환 시작 전 취소 체크 (정식 취소 요청 처리)
                if (cancellationToken != null && cancellationToken.IsCancellationRequested)
                {
                    chunk.Clear();
                    return null;
                }
                
                var mono = new List<double>(chunkSizeMono);
                for (int i = 0; i < chunk.Count; i += 2)
                {
                    if (i + 1 < chunk.Count)
                    {
                        mono.Add((chunk[i] + chunk[i + 1]) / 2.0);
                    }
                    else
                    {
                        mono.Add(chunk[i]);
                    }
                    
                    // 취소 가능한 시점: 메모리 할당 후 (논리적 중단점)
                    // 2000개 샘플마다 체크하여 오버헤드 최소화
                    // 정식 취소 요청: 예외를 throw하지 않고 루프 종료
                    if (cancellationToken != null && cancellationToken.IsCancellationRequested && (i + 2) % 2000 == 0)
                    {
                        // 취소 요청 시 루프 중단 (예외 없이)
                        break;
                    }
                }
                
                // 변환 완료 후 취소 상태 확인 (정식 취소 요청 처리)
                if (cancellationToken != null && cancellationToken.IsCancellationRequested)
                {
                    chunk.Clear();
                    mono.Clear();
                    return null;
                }
                
                return mono.ToArray();
            }

            return chunk.ToArray();
        }

        #region 데이터 구조

        /// <summary>
        /// Wave 파일 컨텍스트 (MessagePack Serializer에서 사용하기 위해 internal로 변경)
        /// </summary>
        internal class WaveFileContext
        {
            public short AudioFormat { get; set; }
            public short Channels { get; set; }
            public int SampleRate { get; set; }
            public short BitsPerSample { get; set; }
            public long DataLength { get; set; }
            public long DataStartPosition { get; set; }
            public long TotalSamples { get; set; }
            public TimeSpan Duration { get; set; }
        }

        private class SpectrogramFrame
        {
            public double Time { get; set; }
            public double[] Magnitudes { get; set; }
            public double[] Frequencies { get; set; }
        }

        //private class Peak
        public class Peak
        {
            public double Time { get; set; }
            public double Frequency { get; set; }
            public double Magnitude { get; set; }
        }

        private class SpectrumResult
        {
            public double[] Magnitudes { get; set; }
            public double[] Frequencies { get; set; }
        }

        private class FingerprintHash
        {
            public double Time { get; set; }
            public double Frequency1 { get; set; }
            public double Frequency2 { get; set; }
            public double TimeDelta { get; set; }
        }

        /// <summary>
        /// FingerprintHashData 비교자 (중복 제거용)
        /// </summary>
        private sealed class FingerprintHashDataComparer : IEqualityComparer<FingerprintHashData>
        {
            public bool Equals(FingerprintHashData x, FingerprintHashData y)
            {
                if (ReferenceEquals(x, y)) return true;
                if (x == null || y == null) return false;
                return x.Hash == y.Hash;
            }

            public int GetHashCode(FingerprintHashData obj)
            {
                return obj?.Hash?.GetHashCode() ?? 0;
            }
        }

        #endregion

        #region MessagePack Serializer 인터페이스 및 구현 (JSON Serializer만 유지)


        /// <summary>
        /// MessagePack 형식으로 저장 (SFP_mp.cs의 Serializer 사용, 간접 참조)
        /// SFP_mp.cs와의 직접 연계를 피하기 위해 Factory를 통한 간접 참조만 사용
        /// </summary>
        public static void SavePickedFptsToFileMessagePack(List<FptEntry> fingerprints, string filePath, int sampleRate, int channels, TimeSpan duration, bool hashOnly = false, bool reverseIndex = false)
        {
            var context = new WaveFileContext
            {
                SampleRate = sampleRate,
                Channels = (short)channels,
                Duration = duration,
                // 기타 필드는 기본값 사용
            };
            SaveFingerprintsToFileMessagePack(fingerprints, filePath, context, hashOnly, reverseIndex);
        }

        /// <summary>
        /// MessagePack 형식으로 저장 (내부 메서드 - WaveFileContext 사용)
        /// </summary>
        private static void SaveFingerprintsToFileMessagePack(List<FptEntry> fingerprints, string filePath, WaveFileContext context, bool hashOnly = false, bool bReverseIndex = false, Action<string> statusMsgCbk = null)
        {
            try
            {
                // SFP_mp.cs의 Factory를 통한 간접 참조
                var serializer = AudioViewStudio.Analysis.MessagePackSerializerFactory.Create();
                serializer.Save(fingerprints, filePath, context, hashOnly, bReverseIndex, statusMsgCbk);
            }
            catch
            {
                // MessagePack 저장 실패 시 예외 전파
                throw;
            }
        }
        

        #endregion
    }

    #region 공개 데이터 구조

  
    /// <summary>
    /// Original-FP 추출 진행 상황
    /// </summary>
    public sealed class OriginalFPProgress
    {
        public int ProcessedFrames { get; set; }
        public int TotalFrames { get; set; }
        public double ProgressPercent { get; set; }
        public TimeSpan CurrentTime { get; set; }
        public string CurrentAction { get; set; }
    }

    /// <summary>
    /// Original-FP 추출 결과
    /// </summary>
    public sealed class OriginalFptResult
    {
        public int TotalFingerprints { get; set; }
        public string OutputFilePath { get; set; }
        public bool WasCanceled { get; set; }
        /// <summary>
        /// 추출에 사용된 오디오 파일의 샘플레이트 (Hz)
        /// Live 매칭 시 동일한 샘플레이트를 사용해야 매칭 정확도가 보장됩니다.
        /// </summary>
        public int AudioSampleRate { get; set; }
    }

    /// <summary>
    /// 핑거프린트 엔트리
    /// </summary>
    public sealed class FptEntry
    {
        public int Timestamp { get; set; } // 초 단위
        public List<FingerprintHashData> Hashes { get; set; }
    }

    /// <summary>
    /// 핑거프린트 해시 데이터
    /// </summary>
    public sealed class FingerprintHashData
    {
        public string Hash { get; set; }
        public double Frequency1 { get; set; }
        public double Frequency2 { get; set; }
        public double TimeDelta { get; set; }
        /// <summary>
        /// 해시가 생성된 정확한 시간 (밀리초 단위) - 시간 해상도 개선
        /// Shazam 방식: Offset = 원본 TimeMs - Live TimeMs
        /// </summary>
        public int TimeMs { get; set; }
    }
    /// <summary>
    /// 과다 출현 해시 필터링 설정
    /// </summary>
    public static class HashFilterConfig
    {
        /// <summary>
        /// 해시가 등장할 수 있는 최대 타임스탬프 수
        /// 이 값을 초과하면 해당 해시는 필터링됨
        /// </summary>
        public const int MaxTimestampsPerHash = 50;

        /// <summary>
        /// 전체 타임스탬프 대비 최대 등장 비율
        /// 예: 0.1 = 전체 시간의 10% 이상에서 등장하면 필터링
        /// </summary>
        public const double MaxTimestampRatio = 0.05;  // 5%

        /// <summary>
        /// 필터링 활성화 여부
        /// </summary>
        public const bool EnableFiltering = true;
    }

    /// <summary>
    /// 핑거프린트 매칭 결과
    /// </summary>
    public sealed class FingerprintMatchResult
    {
        public bool IsMatched { get; set; }
        public double Confidence { get; set; }
        public TimeSpan MatchedTime { get; set; }
        public int MatchedHashCount { get; set; }
        public int TotalHashCount { get; set; }
    }

    /// <summary>
    /// 매칭 이벤트 인자
    /// </summary>
    public class MatchEventArgs : EventArgs
    {
        public TimeSpan MatchedTime { get; set; }
        public double Confidence { get; set; }
    }

    #endregion

}
