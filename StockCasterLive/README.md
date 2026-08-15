# StockCaster Live

증권 차트 창을 선택해 RTMP 방송 서버로 실시간 송출하는 Windows 데스크톱 애플리케이션입니다.

## 현재 구현 범위 (0.1)

- 실행 중인 증권 차트/프로그램 창 선택
- 십자가 커서로 방송할 화면 영역 직접 드래그 선택
- 선택 영역과 개인정보 모자이크 위치 자동 저장·재실행 시 복원
- 선택 창 실시간 미리보기
- 계좌번호 등 민감정보 영역 드래그 모자이크(미리보기·송출 동시 적용)
- 720p / 1080p 출력
- 30fps / 60fps 송출
- DirectShow 마이크 장치 선택
- RTMP/RTMPS 서버 주소 및 스트림 키
- 실시간 송출 상태와 방송 시간 표시
- 사용자 설정 저장

## 실행

```powershell
dotnet run --project .\StockCasterLive\StockCasterLive.csproj
```

방송을 시작하려면 FFmpeg가 필요합니다. [FFmpeg 다운로드 안내](https://ffmpeg.org/download.html)에 따라 설치하고 `ffmpeg.exe`를 시스템 `PATH`에 추가하거나 `StockCasterLive\ffmpeg\ffmpeg.exe`에 별도로 배치하세요. 실행 파일은 Git 저장소에 포함하지 않습니다.

방송 시작 전 RTMP 서버 주소와 스트림 키가 필요합니다. 스트림 키는 소스 코드나 Git에 저장하지 마세요.

## 프로젝트 원칙

이 프로젝트는 기존 ScreenCaster와 독립되어 있으며 기존 프로젝트의 파일을 수정하지 않습니다.

- 제작: 에드가씨에스트
- 문의: 010-8447-9973
