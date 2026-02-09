using System;

namespace AudioViewStudio.Analysis
{
    /// <summary>
    /// ★★★ 2026-02-07: 스트리밍 친화적 오디오 전처리 ★★★
    /// - 윈도우 간 상태 유지 (HPF, 엔벨로프, 게이트)
    /// - 부드러운 RMS/게인 변화 (EMA 기반)
    /// - 연속적인 노이즈 바닥 추정
    /// </summary>
    public class StreamingPreprocessor
    {
        // ========== 설정 파라미터 ==========
        private readonly int _sampleRate;
        private readonly double _hpCutoffHz;
        private readonly double _targetRms;
        private readonly double _maxGain;
        private readonly double _baseGateMultiplier;
        private readonly double _attackMs;
        private readonly double _releaseMs;
        private readonly double _clipDrive;
        
        // ========== HPF 상태 ==========
        private double _hpAlpha;
        private double _hpPrevX = 0.0;
        private double _hpPrevY = 0.0;
        
        // ========== RMS/게인 상태 (EMA 기반) ==========
        private double _runningRms = 0.0;
        private double _runningGain = 1.0;
        private const double RmsSmoothingFactor = 0.1;    // RMS 스무딩 계수 (작을수록 부드러움)
        private const double GainSmoothingFactor = 0.05;  // 게인 스무딩 계수
        
        // ========== 노이즈 바닥 상태 (EMA 기반) ==========
        private double _runningNoiseFloor = 0.0;
        private const double NoiseFloorSmoothingFactor = 0.02; // 노이즈 바닥 스무딩 계수
        
        // ========== 게이트 상태 ==========
        private double _envelope = 0.0;
        private bool _gateOpen = false;
        private double _attackCoeff;
        private double _releaseCoeff;
        
        // ========== DC 오프셋 상태 (EMA 기반) ==========
        private double _runningDcOffset = 0.0;
        private const double DcSmoothingFactor = 0.001; // DC 오프셋 스무딩 계수
        
        // ========== 초기화 상태 ==========
        private bool _isInitialized = false;
        private int _warmupSamples = 0;
        private int _requiredWarmupSamples;
        
        /// <summary>
        /// 스트리밍 전처리기 생성
        /// </summary>
        /// <param name="sampleRate">샘플 레이트</param>
        /// <param name="pparam">전처리 파라미터</param>
        public StreamingPreprocessor(int sampleRate, PreprocessParam pparam)
        {
            _sampleRate = sampleRate > 0 ? sampleRate : 48000;
            _hpCutoffHz = Math.Max(10.0, pparam.hpCutoffHz);
            _targetRms = Math.Max(0.001, pparam.targetRms);
            _maxGain = 6.0;
            _baseGateMultiplier = Math.Max(0.1, pparam.baseGateMultiplier);
            _attackMs = Math.Max(0.1, pparam.attackMs);
            _releaseMs = Math.Max(1.0, pparam.releaseMs);
            _clipDrive = Math.Max(0.1, pparam.clipDrive);
            
            // HPF 계수 계산
            double rc = 1.0 / (2.0 * Math.PI * _hpCutoffHz);
            double dt = 1.0 / _sampleRate;
            _hpAlpha = rc / (rc + dt);
            
            // 게이트 계수 계산
            _attackCoeff = Math.Exp(-1.0 / (_sampleRate * _attackMs / 1000.0));
            _releaseCoeff = Math.Exp(-1.0 / (_sampleRate * _releaseMs / 1000.0));
            
            // 워밍업에 필요한 샘플 수 (약 0.5초)
            _requiredWarmupSamples = _sampleRate / 2;
        }
        
        /// <summary>
        /// 상태 초기화 (새 세션 시작 시 호출)
        /// </summary>
        public void Reset()
        {
            _hpPrevX = 0.0;
            _hpPrevY = 0.0;
            _runningRms = 0.0;
            _runningGain = 1.0;
            _runningNoiseFloor = 0.0;
            _envelope = 0.0;
            _gateOpen = false;
            _runningDcOffset = 0.0;
            _isInitialized = false;
            _warmupSamples = 0;
        }
        
        /// <summary>
        /// 스트리밍 전처리 수행 (상태 유지)
        /// </summary>
        /// <param name="samples">입력 샘플</param>
        /// <param name="gateSoftnessMultiplier">게이트 부드러움 계수</param>
        /// <returns>전처리된 샘플</returns>
        public float[] Process(float[] samples, double gateSoftnessMultiplier = 1.0)
        {
            if (samples == null || samples.Length == 0)
            {
                return samples;
            }
            
            if (gateSoftnessMultiplier < 0.1)
            {
                gateSoftnessMultiplier = 0.1;
            }
            
            float[] processed = new float[samples.Length];
            double tanhDen = Math.Tanh(_clipDrive);
            
            // ========== 1단계: DC 오프셋 추정 (EMA) ==========
            double localMean = 0.0;
            for (int i = 0; i < samples.Length; i++)
            {
                localMean += samples[i];
            }
            localMean /= samples.Length;
            
            // DC 오프셋을 부드럽게 업데이트
            if (!_isInitialized)
            {
                _runningDcOffset = localMean;
            }
            else
            {
                _runningDcOffset = DcSmoothingFactor * localMean + (1.0 - DcSmoothingFactor) * _runningDcOffset;
            }
            
            // ========== 2단계: RMS 추정 (EMA) ==========
            double localSumSq = 0.0;
            double tempPrevX = _hpPrevX;
            double tempPrevY = _hpPrevY;
            
            for (int i = 0; i < samples.Length; i++)
            {
                double x = samples[i] - _runningDcOffset;
                double y = _hpAlpha * (tempPrevY + x - tempPrevX);
                tempPrevX = x;
                tempPrevY = y;
                localSumSq += y * y;
            }
            double localRms = Math.Sqrt(localSumSq / samples.Length);
            
            // RMS를 부드럽게 업데이트
            if (!_isInitialized || _runningRms < 1e-6)
            {
                _runningRms = localRms;
            }
            else
            {
                _runningRms = RmsSmoothingFactor * localRms + (1.0 - RmsSmoothingFactor) * _runningRms;
            }
            
            // ========== 3단계: 게인 계산 (부드러운 변화) ==========
            double targetGain = _runningRms > 1e-6 ? (_targetRms / _runningRms) : 1.0;
            targetGain = Math.Min(targetGain, _maxGain);
            
            // 게인을 부드럽게 업데이트
            if (!_isInitialized)
            {
                _runningGain = targetGain;
            }
            else
            {
                _runningGain = GainSmoothingFactor * targetGain + (1.0 - GainSmoothingFactor) * _runningGain;
            }
            
            // ========== 4단계: 노이즈 바닥 추정 (EMA 기반) ==========
            // 하위 20% 진폭을 노이즈 바닥으로 추정 (간소화)
            double localNoiseFloor = localRms * 0.1; // 대략적인 추정
            
            if (!_isInitialized || _runningNoiseFloor < 1e-9)
            {
                _runningNoiseFloor = localNoiseFloor;
            }
            else
            {
                _runningNoiseFloor = NoiseFloorSmoothingFactor * localNoiseFloor + 
                                     (1.0 - NoiseFloorSmoothingFactor) * _runningNoiseFloor;
            }
            
            // 게이트 임계값 계산
            double gateThreshold = _runningNoiseFloor * _baseGateMultiplier / gateSoftnessMultiplier;
            double openThreshold = gateThreshold;
            double closeThreshold = gateThreshold * 0.7;
            
            // ========== 5단계: 샘플별 처리 (상태 유지) ==========
            for (int i = 0; i < samples.Length; i++)
            {
                // HPF 적용 (상태 유지)
                double x = samples[i] - _runningDcOffset;
                double y = _hpAlpha * (_hpPrevY + x - _hpPrevX);
                _hpPrevX = x;
                _hpPrevY = y;
                
                // 게인 적용
                double v = y * _runningGain;
                
                // 엔벨로프 추적 (상태 유지)
                double absV = Math.Abs(v);
                if (absV > _envelope)
                {
                    _envelope = _attackCoeff * (_envelope - absV) + absV;
                }
                else
                {
                    _envelope = _releaseCoeff * (_envelope - absV) + absV;
                }
                
                // 히스테리시스 게이트 (상태 유지)
                if (!_gateOpen && _envelope >= openThreshold)
                {
                    _gateOpen = true;
                }
                else if (_gateOpen && _envelope <= closeThreshold)
                {
                    _gateOpen = false;
                }
                
                // 워밍업 기간에는 게이트 비활성화
                if (_warmupSamples < _requiredWarmupSamples)
                {
                    _warmupSamples++;
                    // 워밍업 중에는 게이트 없이 통과
                }
                else if (!_gateOpen)
                {
                    v = 0.0;
                }
                
                // 소프트 리미터
                v = Math.Tanh(v * _clipDrive) / tanhDen;
                processed[i] = (float)v;
            }
            
            _isInitialized = true;
            
            return processed;
        }
        
        /// <summary>
        /// 현재 상태 정보 (디버그용)
        /// </summary>
        public string GetStatusInfo()
        {
            return $"RMS:{_runningRms:F4}, Gain:{_runningGain:F2}x, NoiseFloor:{_runningNoiseFloor:F4}, Gate:{(_gateOpen ? "OPEN" : "CLOSED")}";
        }
    }
}

