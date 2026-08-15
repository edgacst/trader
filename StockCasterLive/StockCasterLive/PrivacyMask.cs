namespace StockCasterLive;

/// <summary>
/// 원본 방송 화면을 기준으로 0~1 범위에 저장되는 개인정보 가림 영역입니다.
/// </summary>
public sealed record PrivacyMask(double X, double Y, double Width, double Height);
