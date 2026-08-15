# StockCaster

증권 차트 화면을 실시간으로 송출하고 회원이 웹에서 시청할 수 있도록 구성한 방송 프로젝트입니다.

## 프로젝트 구성

- `StockCasterLive`: 방송 진행자의 Windows 화면을 선택하고 모자이크·마이크와 함께 RTMP로 송출하는 WPF 앱
- `StockCasterPlatform`: 회원가입, 라이브 시청, 채팅, 다시보기, 방송·회원 관리를 제공하는 ASP.NET Core 웹 플랫폼

## 로컬 실행 순서

1. [StockCasterPlatform 실행 안내](StockCasterPlatform/README.md)에 따라 MediaMTX와 최초 관리자 비밀번호를 준비합니다.
2. `StockCasterPlatform/Start StockCaster Platform.cmd`를 실행합니다.
3. 브라우저에서 `http://127.0.0.1:5075/`를 엽니다.
4. [StockCaster Live 실행 안내](StockCasterLive/README.md)에 따라 FFmpeg를 준비하고 송출 앱을 실행합니다.
5. 운영자 송출실에 표시되는 서버 URL과 보호된 스트림 키를 StockCaster Live에 입력한 뒤 방송을 시작합니다.

## 저장소에 포함하지 않는 파일

보안을 위해 회원 데이터베이스, 송출 보안키, 녹화 영상, 로그와 인증서는 Git에 올리지 않습니다. FFmpeg와 MediaMTX 실행 파일도 용량과 배포 라이선스 관리를 위해 별도로 설치합니다.

외부 공개 전에는 HTTPS, 방화벽, 안전한 비밀정보 저장, 백업, 개인정보처리방침과 동시 접속 부하 시험이 필요합니다.

제작: 에드가씨에스트<br>
문의: 010-8447-9973
