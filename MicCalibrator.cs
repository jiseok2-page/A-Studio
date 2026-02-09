using System;
using System.Collections.Generic;

namespace AudioViewStudio
{
    /// <summary>
    /// 마이크 캘리브레이션 클래스
    /// 2초간 테스트 녹음을 통해 적정 게인을 자동 결정
    /// </summary>
    public class MicCalibrator
    {
        // ★ 캘리브레이션 상수 ★
        private const float TargetRms = 0.1f;       // 타겟 RMS 레벨
        private const float MaxGain = 16.0f;        // 최대 게인 제한
        private const float MinGain = 0.5f;         // 최소 게인 제한
        private const float DefaultGain = 4.0f;    // 기본 게인 (무음 시)
        private const int CalibrationDurationSeconds = 2; // 캘리브레이션 시간 (초)
        
        // ★ 상태 변수 ★
        private List<float> _buffer;
        private int _sampleRate;
        private bool _isCalibrating;
        private float _calibratedGain;
        
        /// <summary>
        /// 캘리브레이션 진행 중 여부
        /// </summary>
        public bool IsCalibrating => _isCalibrating;
        
        /// <summary>
        /// 캘리브레이션된 게인 값
        /// </summary>
        public float CalibratedGain => _calibratedGain;
        
        /// <summary>
        /// 캘리브레이션 완료 이벤트
        /// </summary>
        public event Action<float, float> CalibrationCompleted; // (rms, gain)

        public MicCalibrator()
        {
            _calibratedGain = 1.0f;
            _isCalibrating = false;
        }
        
        /// <summary>
        /// 캘리브레이션 시작
        /// </summary>
        /// <param name="sampleRate">샘플레이트</param>
        public void Start(int sampleRate)
        {
            _sampleRate = sampleRate;
            _buffer = new List<float>();
            _calibratedGain = 1.0f;
            _isCalibrating = true;
            
            System.Diagnostics.Debug.WriteLine("[MicCalibrator] 시작 (2초 테스트 녹음)");
        }
        
        /// <summary>
        /// 오디오 샘플 추가 및 캘리브레이션 처리
        /// </summary>
        /// <param name="samples">입력 샘플</param>
        /// <returns>캘리브레이션 완료 여부</returns>
        public bool ProcessSamples(float[] samples)
        {
            if (!_isCalibrating) return false;
            
            _buffer.AddRange(samples);
            int requiredSamples = _sampleRate * CalibrationDurationSeconds;
            
            if (_buffer.Count >= requiredSamples)
            {
                // 캘리브레이션 완료 - RMS 계산 및 게인 결정
                float rms = CalculateRms(_buffer.ToArray());
                
                if (rms > 0.001f) // 무음이 아닌 경우
                {
                    _calibratedGain = TargetRms / rms;
                    _calibratedGain = Math.Max(MinGain, Math.Min(MaxGain, _calibratedGain));
                }
                else
                {
                    _calibratedGain = DefaultGain;
                }
                
                _isCalibrating = false;
                _buffer = null;
                
                System.Diagnostics.Debug.WriteLine($"[MicCalibrator] 완료! RMS: {rms:F4} → 게인: {_calibratedGain:F1}x");
                CalibrationCompleted?.Invoke(rms, _calibratedGain);
                
                return true; // 캘리브레이션 완료
            }
            
            return false; // 아직 진행 중
        }
        
        /// <summary>
        /// 캘리브레이션된 게인 적용
        /// </summary>
        public void ApplyGain(float[] samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= _calibratedGain;
                // 클리핑 방지
                if (samples[i] > 1.0f) samples[i] = 1.0f;
                if (samples[i] < -1.0f) samples[i] = -1.0f;
            }
        }
        
        /// <summary>
        /// RMS 계산
        /// </summary>
        private float CalculateRms(float[] samples)
        {
            if (samples == null || samples.Length == 0) return 0f;
            
            float sum = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                sum += samples[i] * samples[i];
            }
            return (float)Math.Sqrt(sum / samples.Length);
        }
        
        /// <summary>
        /// 캘리브레이션 리셋
        /// </summary>
        public void Reset()
        {
            _isCalibrating = false;
            _calibratedGain = 1.0f;
            _buffer = null;
        }
    }
}
