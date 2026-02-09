using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using MessagePack;

namespace AudioViewStudio.Analysis
{
    #region MessagePack 데이터 구조 (SFP_mp)

    /// <summary>
    /// MessagePack용 핑거프린트 파일 헤더
    /// 버전 2.0: MessagePack 형식
    /// </summary>
    [MessagePackObject]
    public sealed class FingerprintFileHeader_mp
    {
        [Key(0)]
        public string Version { get; set; } = "2.0";

        [Key(1)]
        public bool Quantized { get; set; } = true;

        [Key(2)]
        public bool Compact { get; set; } = false;

        [Key(3)]
        public int SampleRate { get; set; }

        [Key(4)]
        public int Channels { get; set; }

        [Key(5)]
        public double Duration { get; set; }

        [Key(6)]
        public int TotalFingerprints { get; set; }
    }

    /// <summary>
    /// MessagePack용 핑거프린트 엔트리 (전체 모드)
    /// </summary>
    [MessagePackObject]
    public sealed class FptEntry_mp
    {
        [Key(0)]
        public int Timestamp { get; set; } // 초 단위

        [Key(1)]
        public List<FingerprintHashData_mp> Hashes { get; set; }

        /// <summary>
        /// 기존 FingerprintEntry로 변환
        /// </summary>
        public FptEntry ToFptEntry()
        {
            // 메모리 효율적인 변환: LINQ Select().ToList() 대신 초기 용량을 지정한 리스트 사용
            List<FingerprintHashData> hashes = null;
            if (this.Hashes != null && this.Hashes.Count > 0)
            {
                try
                {
                    hashes = new List<FingerprintHashData>(this.Hashes.Count);
                    foreach (var h in this.Hashes)
                    {
                        // null 체크: ArgumentNullException 방지
                        if (h != null)
                        {
                            var hashData = h.ToFingerprintHashData();
                            if (hashData != null)
                            {
                                hashes.Add(hashData);
                            }
                        }
                    }
                    
                    if (hashes.Count == 0)
                    {
                        hashes = null;
                    }
                }
                catch (OutOfMemoryException)
                {
                    if (hashes != null && hashes.Count > 0)
                    {
                        // 부분 변환된 결과라도 사용
                    }
                    else
                    {
                        hashes = null;
                    }
                }
                catch
                {
                    hashes = null;
                }
            }
            
            return new FptEntry
            {
                Timestamp = this.Timestamp,
                Hashes = hashes ?? new List<FingerprintHashData>()
            };
        }

        /// <summary>
        /// 기존 FingerprintEntry에서 생성
        /// </summary>
        public static FptEntry_mp FromFptEntry(FptEntry entry)
        {
            if (entry == null)
                return null;

            // 메모리 효율적인 변환: LINQ Select().ToList() 대신 초기 용량을 지정한 리스트 사용
            List<FingerprintHashData_mp> hashes = null;
            if (entry.Hashes != null && entry.Hashes.Count > 0)
            {
                try
                {
                    hashes = new List<FingerprintHashData_mp>(entry.Hashes.Count);
                    foreach (var h in entry.Hashes)
                    {
                        // null 체크: ArgumentNullException 방지
                        if (h != null)
                        {
                            var mpHash = FingerprintHashData_mp.FromFingerprintHashData(h);
                            if (mpHash != null)
                            {
                                hashes.Add(mpHash);
                            }
                        }
                    }
                    
                    if (hashes.Count == 0)
                    {
                        hashes = null;
                    }
                }
                catch (OutOfMemoryException)
                {
                    if (hashes != null && hashes.Count > 0)
                    {
                        // 부분 변환된 결과라도 사용
                    }
                    else
                    {
                        hashes = null;
                    }
                }
                catch
                {
                    hashes = null;
                }
            }

            return new FptEntry_mp
            {
                Timestamp = entry.Timestamp,
                Hashes = hashes ?? new List<FingerprintHashData_mp>()
            };
        }
    }

    /// <summary>
    /// MessagePack용 핑거프린트 엔트리 (Compact 모드)
    /// </summary>
    [MessagePackObject]
    public sealed class FptEntry_Compact
    {
        [Key(0)]
        public int Timestamp { get; set; } // 초 단위

        [Key(1)]
        public List<FingerprintHashData_Compact> Hashes { get; set; }

        /// <summary>
        /// 기존 FingerprintEntry로 변환
        /// </summary>
        public FptEntry ToFptEntry()
        {
            // 메모리 효율적인 변환: LINQ Select().ToList() 대신 초기 용량을 지정한 리스트 사용
            List<FingerprintHashData> hashes = null;
            if (this.Hashes != null && this.Hashes.Count > 0)
            {
                try
                {
                    hashes = new List<FingerprintHashData>(this.Hashes.Count);
                    foreach (var h in this.Hashes)
                    {
                        // null 체크: ArgumentNullException 방지
                        if (h != null)
                        {
                            var hashData = h.ToFingerprintHashData();
                            if (hashData != null)
                            {
                                hashes.Add(hashData);
                            }
                        }
                    }
                    
                    if (hashes.Count == 0)
                    {
                        hashes = null;
                    }
                }
                catch (OutOfMemoryException)
                {
                    if (hashes != null && hashes.Count > 0)
                    {
                        // 부분 변환된 결과라도 사용
                    }
                    else
                    {
                        hashes = null;
                    }
                }
                catch
                {
                    hashes = null;
                }
            }
            
            return new FptEntry
            {
                Timestamp = this.Timestamp,
                Hashes = hashes ?? new List<FingerprintHashData>()
            };
        }

        /// <summary>
        /// 기존 FingerprintEntry에서 생성
        /// </summary>
        public static FptEntry_Compact FromFptEntry(FptEntry entry)
        {
            if (entry == null)
                return null;

            // 메모리 효율적인 변환: LINQ Select().ToList() 대신 초기 용량을 지정한 리스트 사용
            List<FingerprintHashData_Compact> hashes = null;
            if (entry.Hashes != null && entry.Hashes.Count > 0)
            {
                try
                {
                    hashes = new List<FingerprintHashData_Compact>(entry.Hashes.Count);
                    foreach (var h in entry.Hashes)
                    {
                        // null 체크: ArgumentNullException 방지
                        if (h != null)
                        {
                            var compactHash = FingerprintHashData_Compact.FromFingerprintHashData(h);
                            if (compactHash != null)
                            {
                                hashes.Add(compactHash);
                            }
                        }
                    }
                    
                    if (hashes.Count == 0)
                    {
                        hashes = null;
                    }
                }
                catch (OutOfMemoryException)
                {
                    if (hashes != null && hashes.Count > 0)
                    {
                        // 부분 변환된 결과라도 사용
                    }
                    else
                    {
                        hashes = null;
                    }
                }
                catch
                {
                    hashes = null;
                }
            }

            return new FptEntry_Compact
            {
                Timestamp = entry.Timestamp,
                Hashes = hashes ?? new List<FingerprintHashData_Compact>()
            };
        }
    }

    /// <summary>
    /// MessagePack용 핑거프린트 엔트리 (Compact 모드 + Delta Encoding)
    /// 버전 2.4: 타임스탬프 Delta Encoding으로 용량 절감
    /// </summary>
    [MessagePackObject]
    public sealed class FptEntry_Compact_Delta
    {
        [Key(0)]
        public int TimestampDelta { get; set; } // 이전 타임스탬프와의 차이 (보통 0 또는 1)

        [Key(1)]
        public List<FingerprintHashData_Compact> Hashes { get; set; }

        /// <summary>
        /// Delta로부터 절대 타임스탬프로 변환
        /// </summary>
        public int ToAbsoluteTimestamp(int previousTimestamp)
        {
            return previousTimestamp + TimestampDelta;
        }

        /// <summary>
        /// 절대 타임스탬프로부터 Delta 생성
        /// </summary>
        public static FptEntry_Compact_Delta FromFptEntry(FptEntry entry, int previousTimestamp)
        {
            if (entry == null)
                return null;

            // 메모리 효율적인 변환: LINQ Select().ToList() 대신 초기 용량을 지정한 리스트 사용
            List<FingerprintHashData_Compact> hashes = null;
            if (entry.Hashes != null && entry.Hashes.Count > 0)
            {
                try
                {
                    // 초기 용량을 지정하여 메모리 재할당 최소화
                    hashes = new List<FingerprintHashData_Compact>(entry.Hashes.Count);
                    foreach (var h in entry.Hashes)
                    {
                        // null 체크: ArgumentNullException 방지
                        if (h != null)
                        {
                            var compactHash = FingerprintHashData_Compact.FromFingerprintHashData(h);
                            if (compactHash != null)
                            {
                                hashes.Add(compactHash);
                            }
                        }
                    }
                    
                    // 빈 리스트인 경우 null로 설정 (메모리 절약)
                    if (hashes.Count == 0)
                    {
                        hashes = null;
                    }
                }
                catch (OutOfMemoryException)
                {
                    // 메모리 부족 시 부분 변환 결과라도 반환하거나 null 반환
                    if (hashes != null && hashes.Count > 0)
                    {
                        // 부분 변환된 결과라도 사용
                    }
                    else
                    {
                        hashes = null;
                    }
                }
                catch
                {
                    // 기타 예외 시 null로 설정
                    hashes = null;
                }
            }

            return new FptEntry_Compact_Delta
            {
                TimestampDelta = entry.Timestamp - previousTimestamp,
                Hashes = hashes ?? new List<FingerprintHashData_Compact>()
            };
        }
    }

    /// <summary>
    /// MessagePack용 핑거프린트 해시 데이터 (전체 모드)
    /// 버전 2.1: Hash를 ulong으로 최적화
    /// </summary>
    [MessagePackObject]
    public sealed class FingerprintHashData_mp
    {
        [Key(0)]
        public ulong Hash { get; set; } // ulong (8바이트) - MessagePack varint로 인코딩되어 더 작아질 수 있음

        [Key(1)]
        public float Frequency1 { get; set; }

        [Key(2)]
        public float Frequency2 { get; set; }

        [Key(3)]
        public float TimeDelta { get; set; }

        /// <summary>
        /// 해시가 생성된 정확한 시간 (밀리초 단위)
        /// Shazam 방식: Offset = 원본 TimeMs - Live TimeMs
        /// </summary>
        [Key(4)]
        public int TimeMs { get; set; }

        /// <summary>
        /// 기존 FingerprintHashData로 변환
        /// </summary>
        public FingerprintHashData ToFingerprintHashData()
        {
            return new FingerprintHashData
            {
                Hash = UlongToHexString(this.Hash),
                Frequency1 = this.Frequency1,
                Frequency2 = this.Frequency2,
                TimeDelta = this.TimeDelta,
                TimeMs = this.TimeMs
            };
        }

        /// <summary>
        /// 기존 FingerprintHashData에서 생성
        /// </summary>
        public static FingerprintHashData_mp FromFingerprintHashData(FingerprintHashData hashData)
        {
            if (hashData == null)
                return null;

            return new FingerprintHashData_mp
            {
                Hash = HexStringToUlong(hashData.Hash ?? ""),
                Frequency1 = (float)hashData.Frequency1,
                Frequency2 = (float)hashData.Frequency2,
                TimeDelta = (float)hashData.TimeDelta,
                TimeMs = hashData.TimeMs
            };
        }

        /// <summary>
        /// 16진수 문자열을 ulong으로 변환
        /// 예: "00000000B68F8255" → ulong
        /// </summary>
        internal static ulong HexStringToUlong(string hexString)
        {
            if (string.IsNullOrEmpty(hexString))
                return 0UL;

            // 메모리 효율적인 변환: Substring/PadLeft 대신 직접 처리
            // 16진수 문자열을 ulong으로 변환
            // "00000000B68F8255" → 16자리 → ulong
            try
            {
                if (hexString.Length == 16)
                {
                    // 정확한 길이면 직접 변환 (가장 효율적)
                    return Convert.ToUInt64(hexString, 16);
                }
                else if (hexString.Length > 16)
                {
                    // 길이가 길면 앞부분만 사용 (메모리 효율적으로 처리)
                    // Substring 대신 Span<char> 사용 시도, 불가능하면 직접 변환
                    return Convert.ToUInt64(hexString.Substring(0, 16), 16);
                }
                else
                {
                    // 길이가 짧으면 앞에 0 추가 (PadLeft 대신 직접 처리)
                    // 메모리 효율을 위해 필요한 만큼만 처리
                    int padCount = 16 - hexString.Length;
                    char[] padded = new char[16];
                    for (int i = 0; i < padCount; i++)
                        padded[i] = '0';
                    for (int i = 0; i < hexString.Length; i++)
                        padded[padCount + i] = hexString[i];
                    return Convert.ToUInt64(new string(padded), 16);
                }
            }
            catch (Exception)
            {
                // 변환 실패 시 0 반환
                return 0UL;
            }
        }

        /// <summary>
        /// ulong을 16진수 문자열로 변환
        /// 예: ulong → "00000000B68F8255"
        /// </summary>
        internal static string UlongToHexString(ulong hash)
        {
            return hash.ToString("X16");
        }
    }

    /// <summary>
    /// MessagePack용 핑거프린트 해시 데이터 (Compact 모드 - Hash만 저장)
    /// 버전 2.1: 용량 최적화를 위해 Frequency 필드 완전 제거
    /// </summary>
    [MessagePackObject]
    public sealed class FingerprintHashData_Compact
    {
        [Key(0)]
        public ulong Hash { get; set; } // ulong (8바이트) - MessagePack varint로 인코딩되어 더 작아질 수 있음

        /// <summary>
        /// 기존 FingerprintHashData로 변환 (Frequency는 기본값 0)
        /// </summary>
        public FingerprintHashData ToFingerprintHashData()
        {
            return new FingerprintHashData
            {
                Hash = FingerprintHashData_mp.UlongToHexString(this.Hash),
                Frequency1 = 0.0,
                Frequency2 = 0.0,
                TimeDelta = 0.0
            };
        }

        /// <summary>
        /// 기존 FingerprintHashData에서 생성 (Hash만 추출)
        /// </summary>
        public static FingerprintHashData_Compact FromFingerprintHashData(FingerprintHashData hashData)
        {
            if (hashData == null)
                return null;

            return new FingerprintHashData_Compact
            {
                Hash = FingerprintHashData_mp.HexStringToUlong(hashData.Hash ?? "")
            };
        }
    }

    #region 버전 2.0 호환성 (byte[] Hash 형식)

    /// <summary>
    /// 버전 2.0 호환성: byte[] Hash를 사용하는 구 버전 구조
    /// </summary>
    [MessagePackObject]
    internal sealed class FingerprintHashData_v20
    {
        [Key(0)]
        public byte[] Hash { get; set; }

        [Key(1)]
        public float Frequency1 { get; set; }

        [Key(2)]
        public float Frequency2 { get; set; }

        [Key(3)]
        public float TimeDelta { get; set; }

        public FingerprintHashData ToFingerprintHashData()
        {
            return new FingerprintHashData
            {
                Hash = ByteArrayToHexString(this.Hash ?? new byte[8]),
                Frequency1 = this.Frequency1,
                Frequency2 = this.Frequency2,
                TimeDelta = this.TimeDelta
            };
        }

        private static string ByteArrayToHexString(byte[] hash)
        {
            if (hash == null || hash.Length == 0)
                return "0000000000000000";

            byte[] adjustedHash = new byte[8];
            if (hash.Length > 8)
                Array.Copy(hash, 0, adjustedHash, 0, 8);
            else
                Array.Copy(hash, 0, adjustedHash, 0, hash.Length);

            StringBuilder sb = new StringBuilder(16);
            foreach (byte b in adjustedHash)
            {
                sb.Append(b.ToString("X2"));
            }
            return sb.ToString();
        }
    }

    [MessagePackObject]
    internal sealed class FptEntry_v20
    {
        [Key(0)]
        public int Timestamp { get; set; }

        [Key(1)]
        public List<FingerprintHashData_v20> Hashes { get; set; }

        public FptEntry ToFptEntry()
        {
            // 메모리 효율적인 변환: LINQ Select().ToList() 대신 초기 용량을 지정한 리스트 사용
            List<FingerprintHashData> hashes = null;
            if (this.Hashes != null && this.Hashes.Count > 0)
            {
                try
                {
                    hashes = new List<FingerprintHashData>(this.Hashes.Count);
                    foreach (var h in this.Hashes)
                    {
                        // null 체크: ArgumentNullException 방지
                        if (h != null)
                        {
                            var hashData = h.ToFingerprintHashData();
                            if (hashData != null)
                            {
                                hashes.Add(hashData);
                            }
                        }
                    }
                    
                    if (hashes.Count == 0)
                    {
                        hashes = null;
                    }
                }
                catch (OutOfMemoryException)
                {
                    if (hashes != null && hashes.Count > 0)
                    {
                        // 부분 변환된 결과라도 사용
                    }
                    else
                    {
                        hashes = null;
                    }
                }
                catch
                {
                    hashes = null;
                }
            }
            
            return new FptEntry
            {
                Timestamp = this.Timestamp,
                Hashes = hashes ?? new List<FingerprintHashData>()
            };
        }
    }

    [MessagePackObject]
    internal sealed class FingerprintFileData_v20
    {
        [Key(0)]
        public FingerprintFileHeader_mp Header { get; set; }

        [Key(1)]
        public List<FptEntry_v20> Fingerprints { get; set; }
    }

    #endregion

    /// <summary>
    /// MessagePack용 핑거프린트 파일 데이터 구조 (전체 모드)
    /// </summary>
    [MessagePackObject]
    public sealed class FingerprintFileData_mp
    {
        [Key(0)]
        public FingerprintFileHeader_mp Header { get; set; }

        [Key(1)]
        public List<FptEntry_mp> Fingerprints { get; set; }
    }

    /// <summary>
    /// MessagePack용 핑거프린트 파일 데이터 구조 (Compact 모드)
    /// </summary>
    [MessagePackObject]
    public sealed class FingerprintFileData_Compact
    {
        [Key(0)]
        public FingerprintFileHeader_mp Header { get; set; }

        [Key(1)]
        public List<FptEntry_Compact> Fingerprints { get; set; }
    }

    /// <summary>
    /// MessagePack용 핑거프린트 파일 데이터 구조 (역인덱스 모드 - 버전 2.3)
    /// 매칭 성능 향상: Hash → Timestamp 리스트로 저장하여 O(1) 조회
    /// </summary>
    [MessagePackObject]
    public sealed class FingerprintFileData_Indexed
    {
        [Key(0)]
        public FingerprintFileHeader_mp Header { get; set; }

        /// <summary>
        /// 역인덱스: Hash → Timestamp 리스트
        /// 매칭 시 해시 조회가 O(1)로 수행됨
        /// </summary>
        [Key(1)]
        public Dictionary<ulong, List<int>> HashToTimestamps { get; set; }

        /// <summary>
        /// 원본 데이터 (선택적, 필요시에만 사용)
        /// 역인덱스만으로 매칭은 가능하지만, 디버깅이나 상세 정보가 필요할 때 사용
        /// </summary>
        [Key(2)]
        public List<FptEntry_Compact> RawData { get; set; }
    }

    /// <summary>
    /// MessagePack용 핑거프린트 파일 데이터 구조 (역인덱스 + Delta Encoding 모드 - 버전 2.4)
    /// 매칭 성능 향상: Hash → Timestamp 리스트로 저장하여 O(1) 조회
    /// 용량 최적화: 타임스탬프 Delta Encoding으로 추가 용량 절감
    /// </summary>
    [MessagePackObject]
    public sealed class FingerprintFileData_Indexed_Delta
    {
        [Key(0)]
        public FingerprintFileHeader_mp Header { get; set; }

        /// <summary>
        /// 역인덱스: Hash → Timestamp 리스트
        /// 매칭 시 해시 조회가 O(1)로 수행됨
        /// </summary>
        [Key(1)]
        public Dictionary<ulong, List<int>> HashToTimestamps { get; set; }

        /// <summary>
        /// 원본 데이터 (Delta Encoding 적용)
        /// 첫 번째 타임스탬프는 절대값, 이후는 차이만 저장
        /// </summary>
        [Key(2)]
        public int FirstTimestamp { get; set; } // 첫 번째 타임스탬프 (절대값)

        [Key(3)]
        public List<FptEntry_Compact_Delta> RawDataDelta { get; set; } // Delta Encoding된 엔트리들
    }

    /// <summary>
    /// MessagePack용 WaveFileContext
    /// WaveFileContext는 internal이므로 변환 메서드는 SFPFM 클래스 내부에서만 사용
    /// </summary>
    [MessagePackObject]
    public sealed class WaveFileContext_mp
    {
        [Key(0)]
        public short AudioFormat { get; set; }

        [Key(1)]
        public short Channels { get; set; }

        [Key(2)]
        public int SampleRate { get; set; }

        [Key(3)]
        public short BitsPerSample { get; set; }

        [Key(4)]
        public long DataLength { get; set; }

        [Key(5)]
        public long DataStartPosition { get; set; }

        [Key(6)]
        public long TotalSamples { get; set; }

        [Key(7)]
        public double DurationSeconds { get; set; } // TimeSpan을 double로 변환
    }

    #endregion

    #region MessagePack Serializer 인터페이스 및 구현

    /// <summary>
    /// 핑거프린트 직렬화 인터페이스
    /// </summary>
    internal interface IFingerprintSerializer
    {
        /// <summary>
        /// 핑거프린트를 파일로 저장
        /// </summary>
        void Save(List<FptEntry> fingerprints, string filePath, SFPFM.WaveFileContext context, bool hashOnly, bool reverseIndex, Action<string> statusMessageCallback = null);

        /// <summary>
        /// 파일에서 핑거프린트 로드
        /// </summary>
        List<FptEntry> Load(string filePath, out SFPFM.WaveFileContext context);

        /// <summary>
        /// 파일 형식 확인 (이 Serializer가 처리할 수 있는지)
        /// </summary>
        bool CanLoad(string filePath);
    }

    /// <summary>
    /// MessagePack 핑거프린트 Serializer
    /// 버전 2.0: MessagePack 형식
    /// </summary>
    internal sealed class MessagePackFingerprintSerializer : IFingerprintSerializer
    {
        /// <summary>
        /// MessagePack 파일 매직 넘버 확인
        /// </summary>
        public bool CanLoad(string filePath)
        {
            if (!File.Exists(filePath))
            {
                System.Diagnostics.Debug.WriteLine($"CanLoad: 파일이 존재하지 않음: {filePath}");
                return false;
            }

            try
            {
                // 파일 확장자 확인
                string extension = Path.GetExtension(filePath).ToLowerInvariant();
                bool hasMessagePackExtension = (extension == ".mpack" || extension == ".messagepack");

                // 파일 헤더 확인
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fileStream.Length < 2)
                    {
                        System.Diagnostics.Debug.WriteLine($"CanLoad: 파일 크기가 2바이트 미만: {filePath}");
                        return false;
                    }

                    byte[] header = new byte[2];
                    fileStream.Read(header, 0, 2);

                    // Gzip 압축 파일인지 확인 (버전 2.0, 2.1 호환성)
                    bool isGzip = (header[0] == 0x1F && header[1] == 0x8B);
                    System.Diagnostics.Debug.WriteLine($"CanLoad: 파일 헤더 - [0x{header[0]:X2} 0x{header[1]:X2}], Gzip: {isGzip}, 확장자: {extension}");

                    if (isGzip)
                    {
                        // Gzip 압축 해제 후 MessagePack 데이터 확인
                        fileStream.Position = 0;
                        using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress, leaveOpen: true))
                        {
                            byte[] mpHeader = new byte[1];
                            if (gzipStream.Read(mpHeader, 0, 1) == 1)
                            {
                                // MessagePack fixmap (0x82~0x8F) 또는 map16/32 (0xDE~0xDF) 또는 LZ4 압축 확인
                                // LZ4 압축된 경우 ext99 또는 ext98을 사용할 수 있음
                                bool isValid = (mpHeader[0] >= 0x82 && mpHeader[0] <= 0x8F) ||
                                               mpHeader[0] == 0xDE || mpHeader[0] == 0xDF ||
                                               mpHeader[0] == 0xD4 || mpHeader[0] == 0xC7 || mpHeader[0] == 0xC8;
                                System.Diagnostics.Debug.WriteLine($"CanLoad: Gzip 압축 해제 후 헤더 - [0x{mpHeader[0]:X2}], 유효: {isValid}");
                                return isValid;
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"CanLoad: Gzip 압축 해제 후 헤더 읽기 실패");
                            }
                        }
                    }
                    else
                    {
                        // LZ4 압축 또는 비압축 MessagePack 파일
                        // MessagePack fixmap, map16/32, 또는 LZ4 ext 확인
                        bool isValid = (header[0] >= 0x82 && header[0] <= 0x8F) ||
                                       header[0] == 0xDE || header[0] == 0xDF ||
                                       header[0] == 0xD4 || header[0] == 0xC7 || header[0] == 0xC8;

                        // 확장자가 .mpack이면 헤더가 맞지 않아도 true 반환 (MessagePack 라이브러리가 처리 가능)
                        if (!isValid && hasMessagePackExtension)
                        {
                            System.Diagnostics.Debug.WriteLine($"CanLoad: 헤더가 유효하지 않지만 확장자가 .mpack이므로 true 반환");
                            return true;
                        }

                        System.Diagnostics.Debug.WriteLine($"CanLoad: 비압축 파일 헤더 검사 결과 - 유효: {isValid}");
                        return isValid;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CanLoad: 예외 발생 - {ex.GetType().Name}: {ex.Message}");
                return false;
            }

            System.Diagnostics.Debug.WriteLine($"CanLoad: 기본값 false 반환");
            return false;
        }

        /// <summary>
        /// 핑거프린트를 MessagePack 형식으로 저장
        /// 버전 2.1: Hash를 ulong으로 최적화, Compact 모드에서 Frequency 필드 완전 제거
        /// </summary>
        public void Save(List<FptEntry> fingerprints, string filePath, SFPFM.WaveFileContext context, bool hashOnly, bool bReverseIndex, Action<string> statusMessageCallback = null)
        {
            if (fingerprints == null)
                throw new ArgumentNullException(nameof(fingerprints));
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentNullException(nameof(filePath));
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            // 출력 디렉토리 생성
            string directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempFilePath = filePath + ".tmp";

            // 저장 시작 메시지
            if (statusMessageCallback != null)
            {
                try
                {
                    statusMessageCallback($"파일 저장 중... ({fingerprints.Count}개 엔트리, {Path.GetFileName(filePath)})");
                }
                catch { }
            }

            try
            {
                // 1. 데이터 변환: 기존 형식 → MessagePack 형식
                var header_mp = new FingerprintFileHeader_mp
                {
                    Version = "2.4", // 버전 업데이트: 역인덱스 구조 + Delta Encoding
                    Quantized = true,
                    Compact = hashOnly,
                    SampleRate = context.SampleRate,
                    Channels = context.Channels,
                    Duration = context.Duration.TotalSeconds,
                    TotalFingerprints = fingerprints.Count
                };

                byte[] messagePackData;

                // LZ4 압축 옵션 설정 (매칭 성능 향상: Gzip 대비 3-10배 빠른 압축/해제)
                var options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);

                // Compact 모드: 역인덱스 구조 + Delta Encoding 사용
                if (hashOnly)
                {
                    if (statusMessageCallback != null)
                    {
                        try
                        {
                            statusMessageCallback($"데이터 변환 중... (정렬 및 Delta Encoding, {fingerprints.Count}개 엔트리)");
                        }
                        catch { }
                    }

                    // 메모리 효율적인 정렬: OrderBy().ToList() 대신 직접 정렬
                    // 타임스탬프 순서대로 정렬 (Delta Encoding을 위해 필요)
                    List<FptEntry> sortedFingerprints;
                    try
                    {
                        // 먼저 정렬되어 있는지 확인 (이미 정렬되어 있으면 정렬 건너뛰기)
                        bool isAlreadySorted = true;
                        for (int i = 1; i < fingerprints.Count && isAlreadySorted; i++)
                        {
                            if (fingerprints[i - 1] != null && fingerprints[i] != null &&
                                fingerprints[i - 1].Timestamp > fingerprints[i].Timestamp)
                            {
                                isAlreadySorted = false;
                                break;
                            }
                        }

                        if (isAlreadySorted)
                        {
                            // 이미 정렬되어 있으면 원본 사용 (메모리 절약)
                            sortedFingerprints = fingerprints;
                        }
                        else
                        {
                            // 정렬 필요: 원본 리스트 복사 후 정렬 (원본 보존)
                            sortedFingerprints = new List<FptEntry>(fingerprints.Count);
                            foreach (var entry in fingerprints)
                            {
                                if (entry != null)
                                {
                                    sortedFingerprints.Add(entry);
                                }
                            }
                            sortedFingerprints.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
                        }
                    }
                    catch (OutOfMemoryException)
                    {
                        // 메모리 부족 시 정렬 없이 원본 사용
                        sortedFingerprints = fingerprints;
                    }

                    if (statusMessageCallback != null)
                    {
                        try
                        {
                            statusMessageCallback($"Delta Encoding 및 역인덱스 구축 중... ({sortedFingerprints.Count}개 엔트리)");
                        }
                        catch { }
                    }

                    Dictionary<ulong, List<int>> hashToTimestamps;
                    var fingerprints_delta = new List<FptEntry_Compact_Delta>(sortedFingerprints.Count);
                    int previousTimestamp = 0;
                    bool isFirstEntry = true;
                    int firstTimestamp = 0;
                    int processedEntries = 0;
                    bool indexBuildStopped = false;

                    if (bReverseIndex)
                    {
                        hashToTimestamps = SFPFM.BuildFilteredReverseIndex(sortedFingerprints, statusMessageCallback);
                        foreach (var entry in sortedFingerprints)
                        {
                            if (entry == null) continue;

                            if (isFirstEntry)
                            {
                                previousTimestamp = entry.Timestamp;
                                firstTimestamp = entry.Timestamp;
                                isFirstEntry = false;
                                // 첫 번째 엔트리는 Delta가 0 (자기 자신과의 차이)
                            }

                            var entry_delta = FptEntry_Compact_Delta.FromFptEntry(entry, previousTimestamp);
                            if (entry_delta != null)
                            {
                                fingerprints_delta.Add(entry_delta);
                            }

                            previousTimestamp = entry.Timestamp;
                        }
                        processedEntries = sortedFingerprints.Count;
                    }
                    else
                    {
                        // 역인덱스 구축: Hash → Timestamp 리스트
                        // 메모리 사용량 제한: 최대 5백만 개 해시까지만 역인덱스 구축
                        const int MAX_INDEXED_HASHES = 5_000_000;
                        const long MAX_MEMORY_BYTES = 2L * 1024L * 1024L * 1024L; // 2GB
                        hashToTimestamps = new Dictionary<ulong, List<int>>();
                        DateTime lastReportTime = DateTime.Now;
                        int lastReportedPercent = -1;

                        foreach (var entry in sortedFingerprints)
                        {
                            if (entry == null || entry.Hashes == null) continue;

                            try
                            {
                                // Delta Encoding: 첫 번째는 절대값으로 FirstTimestamp에 저장, 이후는 차이만 저장
                                if (isFirstEntry)
                                {
                                    previousTimestamp = entry.Timestamp;
                                    firstTimestamp = entry.Timestamp;
                                    isFirstEntry = false;
                                    // 첫 번째 엔트리는 Delta가 0 (자기 자신과의 차이)
                                }

                                var entry_delta = FptEntry_Compact_Delta.FromFptEntry(entry, previousTimestamp);
                                if (entry_delta != null)
                                {
                                    fingerprints_delta.Add(entry_delta);
                                }

                                // 역인덱스 구축 (메모리 사용량이 허용 범위 내일 때만)
                                if (!indexBuildStopped)
                                {
                                    foreach (var hashData in entry.Hashes)
                                    {
                                        if (hashData == null) continue;

                                        // 메모리 사용량 체크: 1000개마다 또는 10만 개 해시마다
                                        if (hashToTimestamps.Count > 0 && (hashToTimestamps.Count % 100000 == 0 || processedEntries % 1000 == 0))
                                        {
                                            long currentMemory = GC.GetTotalMemory(false);
                                            if (currentMemory > MAX_MEMORY_BYTES || hashToTimestamps.Count >= MAX_INDEXED_HASHES)
                                            {
                                                // 메모리 사용량이 너무 크거나 해시 개수가 제한을 초과하면 역인덱스 구축 중단
                                                indexBuildStopped = true;
                                                if (statusMessageCallback != null)
                                                {
                                                    try
                                                    {
                                                        statusMessageCallback($"역인덱스 구축 중단 (메모리 절약: {hashToTimestamps.Count}개 해시까지 구축됨)");
                                                    }
                                                    catch { }
                                                }
                                                break;
                                            }
                                        }

                                        try
                                        {
                                            ulong hash = FingerprintHashData_mp.HexStringToUlong(hashData.Hash ?? "");
                                            if (hash == 0UL) continue; // 유효하지 않은 해시는 건너뜀

                                            if (!hashToTimestamps.TryGetValue(hash, out var timestamps))
                                            {
                                                // 초기 용량을 1로 설정하여 메모리 절약 (대부분의 해시는 1개의 타임스탬프만 가짐)
                                                timestamps = new List<int>(1);
                                                hashToTimestamps[hash] = timestamps;
                                            }
                                            else
                                            {
                                                // 이미 100개 이상이면 추가하지 않음 (메모리 절약)
                                                if (timestamps.Count >= 100)
                                                    continue;
                                            }

                                            timestamps.Add(entry.Timestamp);
                                        }
                                        catch (OutOfMemoryException)
                                        {
                                            // 메모리 부족 시 역인덱스 구축 중단
                                            indexBuildStopped = true;
                                            if (statusMessageCallback != null)
                                            {
                                                try
                                                {
                                                    statusMessageCallback($"역인덱스 구축 중단: 메모리 부족 ({hashToTimestamps.Count}개 해시까지 구축됨)");
                                                }
                                                catch { }
                                            }
                                            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                                            GC.WaitForPendingFinalizers();
                                            break;
                                        }
                                        catch (Exception hashEx)
                                        {
                                            // 해시 변환 실패 시 해당 해시는 건너뜀
                                            System.Diagnostics.Debug.WriteLine($"HexStringToUlong 실패: {hashEx.GetType().Name} - {hashEx.Message}");
                                            continue;
                                        }
                                    }
                                }

                                previousTimestamp = entry.Timestamp;
                                processedEntries++;

                                // 진행 상황 보고 및 메모리 부족 방지: 1000개마다 또는 1%마다 또는 3초마다
                                bool shouldReport = false;
                                int currentPercent = sortedFingerprints.Count > 0 ? (int)((double)processedEntries / sortedFingerprints.Count * 100) : 0;

                                if (processedEntries % 1000 == 0 || currentPercent != lastReportedPercent || (DateTime.Now - lastReportTime).TotalSeconds >= 3.0)
                                {
                                    shouldReport = true;
                                    lastReportTime = DateTime.Now;
                                    lastReportedPercent = currentPercent;
                                }

                                if (shouldReport && statusMessageCallback != null)
                                {
                                    try
                                    {
                                        string indexStatus = indexBuildStopped ? $" (역인덱스: {hashToTimestamps.Count}개)" : $" (역인덱스: {hashToTimestamps.Count}개 구축 중)";
                                        statusMessageCallback($"Delta Encoding 진행 중... ({processedEntries}/{sortedFingerprints.Count}, {currentPercent}%){indexStatus}");
                                    }
                                    catch { }
                                }

                                // 메모리 부족 방지: 2000개마다 GC 실행 (빈도 증가)
                                if (processedEntries % 2000 == 0)
                                {
                                    GC.Collect(0, GCCollectionMode.Optimized, false);
                                }

                                // 메모리 체크: 1만 개마다
                                if (processedEntries % 10000 == 0)
                                {
                                    long currentMemory = GC.GetTotalMemory(false);
                                    if (currentMemory > MAX_MEMORY_BYTES * 0.8) // 80% 이상 사용 시
                                    {
                                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                                        GC.WaitForPendingFinalizers();
                                    }
                                }
                            }
                            catch (OutOfMemoryException)
                            {
                                // 메모리 부족 시 해당 엔트리는 건너뜀
                                try
                                {
                                    if (statusMessageCallback != null)
                                    {
                                        try
                                        {
                                            statusMessageCallback($"메모리 부족으로 일부 엔트리 건너뜀... ({processedEntries}/{sortedFingerprints.Count})");
                                        }
                                        catch { }
                                    }
                                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                                    GC.WaitForPendingFinalizers();

                                    // 역인덱스 구축도 중단
                                    if (!indexBuildStopped)
                                    {
                                        indexBuildStopped = true;
                                        // 메모리 확보를 위해 역인덱스 일부 정리
                                        if (hashToTimestamps.Count > 100000)
                                        {
                                            // 메모리 효율적으로: OrderByDescending 대신 직접 처리
                                            // 랜덤하게 절반만 유지 (정렬 없이 메모리 절약)
                                            int targetCount = hashToTimestamps.Count / 2;
                                            var keysToRemove = new List<ulong>(targetCount);
                                            int removedCount = 0;
                                            foreach (var kvp in hashToTimestamps)
                                            {
                                                if (removedCount >= targetCount) break;
                                                // 타임스탬프 리스트가 큰 항목부터 제거 (메모리 절약)
                                                if (kvp.Value != null && kvp.Value.Count > 5)
                                                {
                                                    keysToRemove.Add(kvp.Key);
                                                    removedCount++;
                                                }
                                            }

                                            // 남은 항목이 부족하면 추가 제거
                                            if (removedCount < targetCount)
                                            {
                                                foreach (var kvp in hashToTimestamps)
                                                {
                                                    if (removedCount >= targetCount) break;
                                                    if (!keysToRemove.Contains(kvp.Key))
                                                    {
                                                        keysToRemove.Add(kvp.Key);
                                                        removedCount++;
                                                    }
                                                }
                                            }

                                            // 제거 실행
                                            foreach (var key in keysToRemove)
                                            {
                                                hashToTimestamps.Remove(key);
                                            }

                                            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                                            GC.WaitForPendingFinalizers();
                                        }
                                    }
                                }
                                catch { }
                                continue;
                            }
                            catch (Exception ex)
                            {
                                // 기타 예외 시 해당 엔트리는 건너뜀
                                System.Diagnostics.Debug.WriteLine($"Delta Encoding 처리 중 예외: {ex.GetType().Name} - {ex.Message}");
                                continue;
                            }
                        }
                    }

                    // 완료 보고
                    if (statusMessageCallback != null)
                    {
                        try
                        {
                            string indexStatus = indexBuildStopped ? $" (역인덱스: {hashToTimestamps.Count}개, 중단됨)" : $" (역인덱스: {hashToTimestamps.Count}개)";
                            statusMessageCallback($"Delta Encoding 완료 ({processedEntries}/{sortedFingerprints.Count}개 처리, {fingerprints_delta.Count}개 변환{indexStatus})");
                        }
                        catch { }
                    }

                    var fileData_indexed_delta = new FingerprintFileData_Indexed_Delta
                    {
                        Header = header_mp,
                        HashToTimestamps = hashToTimestamps,
                        FirstTimestamp = firstTimestamp,
                        RawDataDelta = fingerprints_delta
                    };

                    // 2. MessagePack 직렬화 전 메모리 정리
                    if (statusMessageCallback != null)
                    {
                        try
                        {
                            statusMessageCallback("메모리 정리 중... (직렬화 전)");
                        }
                        catch { }
                    }

                    try
                    {
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                        GC.WaitForPendingFinalizers();
                    }
                    catch { }

                    // 2. MessagePack 직렬화 + LZ4 압축 (역인덱스 + Delta Encoding 모드)
                    if (statusMessageCallback != null)
                    {
                        try
                        {
                            statusMessageCallback($"MessagePack 직렬화 시작... ({fingerprints_delta.Count}개 엔트리, {hashToTimestamps.Count}개 해시)");
                        }
                        catch { }
                    }

                    DateTime serializeStartTime = DateTime.Now;
                    try
                    {
                        messagePackData = MessagePackSerializer.Serialize(fileData_indexed_delta, options);

                        TimeSpan serializeElapsed = DateTime.Now - serializeStartTime;
                        if (statusMessageCallback != null)
                        {
                            try
                            {
                                statusMessageCallback($"MessagePack 직렬화 완료 ({messagePackData.Length / 1024 / 1024}MB, 소요 시간: {serializeElapsed.TotalSeconds:F1}초)");
                            }
                            catch { }
                        }
                    }
                    catch (OutOfMemoryException)
                    {
                        if (statusMessageCallback != null)
                        {
                            try
                            {
                                statusMessageCallback($"MessagePack 직렬화 실패: 메모리 부족");
                            }
                            catch { }
                        }
                        throw;
                    }
                    catch (Exception serializeEx)
                    {
                        if (statusMessageCallback != null)
                        {
                            try
                            {
                                statusMessageCallback($"MessagePack 직렬화 실패: {serializeEx.GetType().Name} - {serializeEx.Message}");
                            }
                            catch { }
                        }
                        throw;
                    }
                }
                else
                {
                    if (statusMessageCallback != null)
                    {
                        try
                        {
                            statusMessageCallback($"데이터 변환 중... (전체 모드, {fingerprints.Count}개 엔트리)");
                        }
                        catch { }
                    }

                    // 전체 모드: Hash + Frequency 필드 모두 저장 (역인덱스 없음)
                    // 메모리 효율적인 변환: LINQ Select().ToList() 대신 직접 변환
                    var fingerprints_mp = new List<FptEntry_mp>(fingerprints.Count);
                    int processedEntries = 0;
                    DateTime lastReportTime = DateTime.Now;
                    int lastReportedPercent = -1;

                    foreach (var entry in fingerprints)
                    {
                        if (entry == null) continue;

                        try
                        {
                            var mpEntry = FptEntry_mp.FromFptEntry(entry);
                            if (mpEntry != null)
                            {
                                fingerprints_mp.Add(mpEntry);
                            }
                            processedEntries++;

                            // 진행 상황 보고 및 메모리 부족 방지: 1000개마다 또는 1%마다 또는 3초마다
                            bool shouldReport = false;
                            int currentPercent = fingerprints.Count > 0 ? (int)((double)processedEntries / fingerprints.Count * 100) : 0;

                            if (processedEntries % 1000 == 0 || currentPercent != lastReportedPercent || (DateTime.Now - lastReportTime).TotalSeconds >= 3.0)
                            {
                                shouldReport = true;
                                lastReportTime = DateTime.Now;
                                lastReportedPercent = currentPercent;
                            }

                            if (shouldReport && statusMessageCallback != null)
                            {
                                try
                                {
                                    statusMessageCallback($"데이터 변환 진행 중... ({processedEntries}/{fingerprints.Count}, {currentPercent}%)");
                                }
                                catch { }
                            }

                            // 메모리 부족 방지: 5000개마다 GC 실행
                            if (processedEntries % 5000 == 0)
                            {
                                GC.Collect(0, GCCollectionMode.Optimized, false);
                            }
                        }
                        catch (OutOfMemoryException)
                        {
                            // 메모리 부족 시 해당 엔트리는 건너뜀
                            try
                            {
                                if (statusMessageCallback != null)
                                {
                                    try
                                    {
                                        statusMessageCallback($"메모리 부족으로 일부 엔트리 건너뜀... ({processedEntries}/{fingerprints.Count})");
                                    }
                                    catch { }
                                }
                                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                                GC.WaitForPendingFinalizers();
                            }
                            catch { }
                            continue;
                        }
                        catch (Exception ex)
                        {
                            // 기타 예외 시 해당 엔트리는 건너뜀
                            System.Diagnostics.Debug.WriteLine($"데이터 변환 중 예외: {ex.GetType().Name} - {ex.Message}");
                            continue;
                        }
                    }

                    // 완료 보고
                    if (statusMessageCallback != null)
                    {
                        try
                        {
                            statusMessageCallback($"데이터 변환 완료 ({processedEntries}/{fingerprints.Count}개 처리, {fingerprints_mp.Count}개 변환)");
                        }
                        catch { }
                    }

                    var fileData_mp = new FingerprintFileData_mp
                    {
                        Header = header_mp,
                        Fingerprints = fingerprints_mp
                    };

                    // 2. MessagePack 직렬화 전 메모리 정리
                    if (statusMessageCallback != null)
                    {
                        try
                        {
                            statusMessageCallback("메모리 정리 중... (직렬화 전)");
                        }
                        catch { }
                    }

                    try
                    {
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                        GC.WaitForPendingFinalizers();
                    }
                    catch { }

                    // 2. MessagePack 직렬화 + LZ4 압축 (전체 모드)
                    if (statusMessageCallback != null)
                    {
                        try
                        {
                            statusMessageCallback($"MessagePack 직렬화 시작... ({fingerprints_mp.Count}개 엔트리)");
                        }
                        catch { }
                    }

                    DateTime serializeStartTime = DateTime.Now;
                    try
                    {
                        messagePackData = MessagePackSerializer.Serialize(fileData_mp, options);

                        TimeSpan serializeElapsed = DateTime.Now - serializeStartTime;
                        if (statusMessageCallback != null)
                        {
                            try
                            {
                                statusMessageCallback($"MessagePack 직렬화 완료 ({messagePackData.Length / 1024 / 1024}MB, 소요 시간: {serializeElapsed.TotalSeconds:F1}초)");
                            }
                            catch { }
                        }
                    }
                    catch (OutOfMemoryException)
                    {
                        if (statusMessageCallback != null)
                        {
                            try
                            {
                                statusMessageCallback($"MessagePack 직렬화 실패: 메모리 부족");
                            }
                            catch { }
                        }
                        throw;
                    }
                    catch (Exception serializeEx)
                    {
                        if (statusMessageCallback != null)
                        {
                            try
                            {
                                statusMessageCallback($"MessagePack 직렬화 실패: {serializeEx.GetType().Name} - {serializeEx.Message}");
                            }
                            catch { }
                        }
                        throw;
                    }
                }

                // 3. LZ4 압축이 MessagePack 내부에서 처리되므로 파일에 직접 저장
                if (statusMessageCallback != null)
                {
                    try
                    {
                        statusMessageCallback($"파일 쓰기 중... ({messagePackData.Length / 1024 / 1024}MB, {Path.GetFileName(filePath)})");
                    }
                    catch { }
                }

                using (var fileStream = new FileStream(tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192))
                {
                    fileStream.Write(messagePackData, 0, messagePackData.Length);
                    fileStream.Flush();
                }

                // 4. 원자적 파일 교체
                if (statusMessageCallback != null)
                {
                    try
                    {
                        statusMessageCallback("최종 파일 교체 중...");
                    }
                    catch { }
                }

                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
                File.Move(tempFilePath, filePath);

                // 저장 완료 메시지
                if (statusMessageCallback != null)
                {
                    try
                    {
                        statusMessageCallback($"파일 저장 완료: {Path.GetFileName(filePath)}");
                    }
                    catch { }
                }
            }
            catch (OutOfMemoryException)
            {
                // 메모리 부족 시 임시 파일 정리 및 메모리 정리 후 예외 재발생
                try
                {
                    if (File.Exists(tempFilePath))
                    {
                        File.Delete(tempFilePath);
                    }
                }
                catch { }

                // 메모리 정리
                try
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false);
                }
                catch { }

                throw;
            }
            catch
            {
                // 실패 시 임시 파일 정리
                try
                {
                    if (File.Exists(tempFilePath))
                    {
                        File.Delete(tempFilePath);
                    }
                }
                catch { }
                throw;
            }
        }

        /// <summary>
        /// MessagePack 형식 파일에서 핑거프린트 로드 (기존 방식 - 호환성)
        /// </summary>
        public List<FptEntry> Load(string filePath, out SFPFM.WaveFileContext context)
        {
            return Load(filePath, out context, out _);
        }

        /// <summary>
        /// MessagePack 형식 파일에서 핑거프린트 로드 (역인덱스 포함)
        /// 버전 2.4: 역인덱스 + Delta Encoding 모드 지원
        /// 버전 2.3: 역인덱스 모드 지원 (매칭 성능 향상)
        /// 버전 2.2: Compact 모드와 전체 모드 지원
        /// </summary>
        /// <param name="filePath">파일 경로</param>
        /// <param name="context">WaveFileContext (출력)</param>
        /// <param name="hashToTimestamps">역인덱스 (출력, 선택적)</param>
        /// <returns>핑거프린트 엔트리 리스트</returns>
        public List<FptEntry> Load(string filePath, out SFPFM.WaveFileContext context, out Dictionary<ulong, List<int>> hashToTimestamps)
        {
            context = null;
            hashToTimestamps = null;

            if (!File.Exists(filePath))
                return null;

            try
            {
                // 1. 파일 읽기 (LZ4 압축은 MessagePack 내부에서 자동 처리)
                // 버전 호환성: Gzip과 LZ4 모두 지원
                byte[] messagePackData;
                byte[] fileData;

                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var ms = new MemoryStream())
                {
                    fileStream.CopyTo(ms);
                    fileData = ms.ToArray();
                }

                // Gzip 압축 파일인지 확인 (버전 2.0, 2.1 호환성)
                bool isGzip = fileData.Length >= 2 && fileData[0] == 0x1F && fileData[1] == 0x8B;

                if (isGzip)
                {
                    // Gzip 압축 해제 (구 버전 파일 호환성)
                    using (var ms = new MemoryStream(fileData))
                    using (var gzipStream = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: false))
                    using (var decompressed = new MemoryStream())
                    {
                        gzipStream.CopyTo(decompressed);
                        messagePackData = decompressed.ToArray();
                    }
                }
                else
                {
                    // LZ4 압축 또는 비압축 (MessagePack 내부에서 자동 처리)
                    messagePackData = fileData;
                }

                // 2. 버전 2.4: 역인덱스 + Delta, 2.3: 역인덱스, 2.2: Compact, 2.1: 전체 모드 모두 시도
                // 버전 2.0 호환성도 지원 (byte[] Hash 형식)
                // LZ4 압축 옵션 설정 (MessagePack 내부에서 자동 감지 및 처리)
                var options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);

                // 먼저 버전 2.4 역인덱스 + Delta Encoding 모드로 시도
                try
                {
                    var fileData_indexed_delta = MessagePackSerializer.Deserialize<FingerprintFileData_Indexed_Delta>(messagePackData, options);
                    if (fileData_indexed_delta != null && fileData_indexed_delta.Header != null && fileData_indexed_delta.HashToTimestamps != null)
                    {
                        // 3. WaveFileContext 생성
                        context = new SFPFM.WaveFileContext
                        {
                            SampleRate = fileData_indexed_delta.Header.SampleRate,
                            Channels = (short)fileData_indexed_delta.Header.Channels,
                            Duration = TimeSpan.FromSeconds(fileData_indexed_delta.Header.Duration),
                            // 기타 필드는 기본값 사용 (필요시 확장)
                        };

                        // 4. 역인덱스 저장 (매칭 성능 향상)
                        hashToTimestamps = fileData_indexed_delta.HashToTimestamps;

                        // 5. Delta Encoding 복원: Delta → 절대 타임스탬프 (호환성을 위해)
                        var fingerprints = new List<FptEntry>();
                        int currentTimestamp = fileData_indexed_delta.FirstTimestamp;

                        if (fileData_indexed_delta.RawDataDelta != null)
                        {
                            foreach (var entry_delta in fileData_indexed_delta.RawDataDelta)
                            {
                                currentTimestamp = entry_delta.ToAbsoluteTimestamp(currentTimestamp);

                                // 메모리 효율적인 변환
                                List<FingerprintHashData> hashList = null;
                                if (entry_delta.Hashes != null && entry_delta.Hashes.Count > 0)
                                {
                                    try
                                    {
                                        hashList = new List<FingerprintHashData>(entry_delta.Hashes.Count);
                                        foreach (var h in entry_delta.Hashes)
                                        {
                                            if (h != null)
                                            {
                                                var hashData = h.ToFingerprintHashData();
                                                if (hashData != null)
                                                {
                                                    hashList.Add(hashData);
                                                }
                                            }
                                        }

                                        if (hashList.Count == 0)
                                        {
                                            hashList = null;
                                        }
                                    }
                                    catch (OutOfMemoryException)
                                    {
                                        if (hashList != null && hashList.Count > 0)
                                        {
                                            // 부분 변환된 결과라도 사용
                                        }
                                        else
                                        {
                                            hashList = null;
                                        }
                                    }
                                    catch
                                    {
                                        hashList = null;
                                    }
                                }

                                var entry = new FptEntry
                                {
                                    Timestamp = currentTimestamp,
                                    Hashes = hashList ?? new List<FingerprintHashData>()
                                };
                                fingerprints.Add(entry);
                            }
                        }

                        return fingerprints;
                    }
                }
                catch
                {
                    // Delta Encoding 모드로 읽기 실패 - 무시하고 다음 형식 시도
                }

                // 버전 2.3 역인덱스 모드로 시도
                try
                {
                    var fileData_indexed = MessagePackSerializer.Deserialize<FingerprintFileData_Indexed>(messagePackData, options);
                    if (fileData_indexed != null && fileData_indexed.Header != null && fileData_indexed.HashToTimestamps != null)
                    {
                        // 3. WaveFileContext 생성
                        context = new SFPFM.WaveFileContext
                        {
                            SampleRate = fileData_indexed.Header.SampleRate,
                            Channels = (short)fileData_indexed.Header.Channels,
                            Duration = TimeSpan.FromSeconds(fileData_indexed.Header.Duration),
                            // 기타 필드는 기본값 사용 (필요시 확장)
                        };

                        // 4. 역인덱스 저장 (매칭 성능 향상)
                        hashToTimestamps = fileData_indexed.HashToTimestamps;

                        // 5. 역인덱스를 FingerprintEntry로 변환 (RawData 사용 또는 역인덱스로 재구성)
                        if (fileData_indexed.RawData != null && fileData_indexed.RawData.Count > 0)
                        {
                            // RawData가 있으면 그대로 사용
                            var fingerprints = fileData_indexed.RawData
                                .Select(e => e.ToFptEntry())
                                .ToList();
                            return fingerprints;
                        }
                        else
                        {
                            // RawData가 없으면 역인덱스로부터 재구성
                            // Timestamp → Hash 리스트로 변환
                            var timestampToHashes = new Dictionary<int, List<FingerprintHashData>>();

                            foreach (var kvp in fileData_indexed.HashToTimestamps)
                            {
                                ulong hash = kvp.Key;
                                List<int> timestamps = kvp.Value;

                                string hashString = FingerprintHashData_mp.UlongToHexString(hash);

                                foreach (int timestamp in timestamps)
                                {
                                    if (!timestampToHashes.TryGetValue(timestamp, out var hashList))
                                    {
                                        hashList = new List<FingerprintHashData>();
                                        timestampToHashes[timestamp] = hashList;
                                    }

                                    hashList.Add(new FingerprintHashData
                                    {
                                        Hash = hashString,
                                        Frequency1 = 0.0,
                                        Frequency2 = 0.0,
                                        TimeDelta = 0.0
                                    });
                                }
                            }

                            var fingerprints = timestampToHashes
                                .OrderBy(kvp => kvp.Key)
                                .Select(kvp => new FptEntry
                                {
                                    Timestamp = kvp.Key,
                                    Hashes = kvp.Value
                                })
                                .ToList();

                            return fingerprints;
                        }
                    }
                }
                catch
                {
                    // 역인덱스 모드로 읽기 실패 - 무시하고 다음 형식 시도
                }

                // 버전 2.1/2.2 전체 모드로 시도
                try
                {
                    var fileData_mp = MessagePackSerializer.Deserialize<FingerprintFileData_mp>(messagePackData, options);
                    if (fileData_mp != null && fileData_mp.Header != null && fileData_mp.Fingerprints != null)
                    {
                        // 3. WaveFileContext 생성
                        context = new SFPFM.WaveFileContext
                        {
                            SampleRate = fileData_mp.Header.SampleRate,
                            Channels = (short)fileData_mp.Header.Channels,
                            Duration = TimeSpan.FromSeconds(fileData_mp.Header.Duration),
                            // 기타 필드는 기본값 사용 (필요시 확장)
                        };

                        // 4. 기존 형식으로 변환 (전체 모드)
                        var fingerprints = fileData_mp.Fingerprints
                            .Select(e => e.ToFptEntry())
                            .ToList();

                        return fingerprints;
                    }
                }
                catch
                {
                    // 전체 모드로 읽기 실패 - 무시하고 다음 형식 시도
                }

                // 버전 2.1 Compact 모드로 시도
                try
                {
                    var fileData_compact = MessagePackSerializer.Deserialize<FingerprintFileData_Compact>(messagePackData, options);
                    if (fileData_compact != null && fileData_compact.Header != null && fileData_compact.Fingerprints != null)
                    {
                        // 3. WaveFileContext 생성
                        context = new SFPFM.WaveFileContext
                        {
                            SampleRate = fileData_compact.Header.SampleRate,
                            Channels = (short)fileData_compact.Header.Channels,
                            Duration = TimeSpan.FromSeconds(fileData_compact.Header.Duration),
                            // 기타 필드는 기본값 사용 (필요시 확장)
                        };

                        // 4. 기존 형식으로 변환 (Compact 모드)
                        var fingerprints = fileData_compact.Fingerprints
                            .Select(e => e.ToFptEntry())
                            .ToList();

                        return fingerprints;
                    }
                }
                catch
                {
                    // Compact 모드로도 읽기 실패 - 무시하고 버전 2.0 시도
                }

                // 버전 2.0 호환성: byte[] Hash 형식으로 시도
                try
                {
                    var fileData_v20 = MessagePackSerializer.Deserialize<FingerprintFileData_v20>(messagePackData, options);
                    if (fileData_v20 != null && fileData_v20.Header != null && fileData_v20.Fingerprints != null)
                    {
                        // 3. WaveFileContext 생성
                        context = new SFPFM.WaveFileContext
                        {
                            SampleRate = fileData_v20.Header.SampleRate,
                            Channels = (short)fileData_v20.Header.Channels,
                            Duration = TimeSpan.FromSeconds(fileData_v20.Header.Duration),
                            // 기타 필드는 기본값 사용 (필요시 확장)
                        };

                        // 4. 기존 형식으로 변환 (버전 2.0)
                        var fingerprints = fileData_v20.Fingerprints
                            .Select(e => e.ToFptEntry())
                            .ToList();

                        return fingerprints;
                    }
                }
                catch
                {
                    // 버전 2.0으로도 읽기 실패
                }

                // 모든 형식으로 읽기 실패 시 null 반환
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MessagePackFingerprintSerializer.Load 오류: {ex.GetType().Name} - {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"파일 경로: {filePath}");
                context = null;
                return null;
            }
        }
    }

    #endregion

    /// <summary>
    /// MessagePack Serializer Factory
    /// </summary>
    public static class MessagePackSerializerFactory
    {
        /// <summary>
        /// MessagePack Serializer 인스턴스 생성
        /// </summary>
        internal static IFingerprintSerializer Create()
        {
            return new MessagePackFingerprintSerializer();
        }

        /// <summary>
        /// 역인덱스 구조를 직접 로드합니다 (매칭 성능 최적화)
        /// 버전 2.3 이상의 파일에서만 사용 가능
        /// </summary>
        /// <param name="filePath">핑거프린트 파일 경로</param>
        /// <param name="context">WaveFileContext (출력)</param>
        /// <returns>역인덱스 (Hash → Timestamp 리스트), 파일이 역인덱스 형식이 아니면 null</returns>
        internal static Dictionary<ulong, List<int>> LoadIndexedFingerprints(string filePath, out SFPFM.WaveFileContext context)
        {
            context = null;

            if (!File.Exists(filePath))
                return null;

            try
            {
                // 파일 읽기
                byte[] messagePackData;
                byte[] fileData;
                
                using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var ms = new MemoryStream())
                {
                    fileStream.CopyTo(ms);
                    fileData = ms.ToArray();
                }

                // Gzip 압축 파일인지 확인
                bool isGzip = fileData.Length >= 2 && fileData[0] == 0x1F && fileData[1] == 0x8B;
                
                if (isGzip)
                {
                    // Gzip 압축 해제
                    using (var ms = new MemoryStream(fileData))
                    using (var gzipStream = new GZipStream(ms, CompressionMode.Decompress, leaveOpen: false))
                    using (var decompressed = new MemoryStream())
                    {
                        gzipStream.CopyTo(decompressed);
                        messagePackData = decompressed.ToArray();
                    }
                }
                else
                {
                    messagePackData = fileData;
                }

                // LZ4 압축 옵션 설정
                var options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);

                // 역인덱스 모드로 시도
                try
                {
                    var fileData_indexed = MessagePackSerializer.Deserialize<FingerprintFileData_Indexed>(messagePackData, options);
                    if (fileData_indexed != null && fileData_indexed.Header != null && fileData_indexed.HashToTimestamps != null)
                    {
                        context = new SFPFM.WaveFileContext
                        {
                            SampleRate = fileData_indexed.Header.SampleRate,
                            Channels = (short)fileData_indexed.Header.Channels,
                            Duration = TimeSpan.FromSeconds(fileData_indexed.Header.Duration),
                        };

                        return fileData_indexed.HashToTimestamps;
                    }
                }
                catch
                {
                    // 역인덱스 모드가 아님
                }
            }
            catch
            {
                // 로드 실패
            }

            return null;
        }
    }
}

