namespace StockCasterLive;

/// <summary>
/// 메인 방송 화면 위에 작게 띄워 보여줄 추가 캡처 영역.
///
/// 화면의 '절대 좌표'를 그대로 기억하지 않고, 특정 창(SourceWindowHandle)을 기준으로
/// 한 '상대 위치'로 기억한다 — OBS의 "창 캡처 + 자르기 필터"와 같은 방식이다. 예를
/// 들어 호가주문 창의 오른쪽 위 20% 영역을 오버레이로 지정해두면, 나중에 사용자가
/// 그 창을 옮기거나 크기를 바꿔도(SourceWindowHandle의 현재 위치를 매 프레임 다시
/// 확인하므로) 오버레이가 알아서 같은 자리를 따라간다. 창이 닫혔거나 최소화된
/// 프레임에는 그 오버레이만 건너뛴다(메인 화면은 그대로 보임).
///
/// SourceCropX/Y/Width/Height: 그 창 안에서 0~1 비율로 어디를 잘라올지.
/// X/Y/Width/Height: 최종 송출 화면 안에서 0~1 비율로 어디에 얼마나 크게 보여줄지
/// (PrivacyMask와 동일한 정규화 좌표 방식).
/// </summary>
public sealed record OverlayRegion(
    nint SourceWindowHandle,
    string SourceWindowTitle,
    double SourceCropX,
    double SourceCropY,
    double SourceCropWidth,
    double SourceCropHeight,
    double X,
    double Y,
    double Width,
    double Height);
