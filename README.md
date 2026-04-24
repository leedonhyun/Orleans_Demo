# Orleans_Demo

Orleans + SignalR 기반 실시간 채팅 데모 프로젝트입니다.

- Backend: ASP.NET Core (`net10.0`)
- Actor Model: Orleans
- Storage/Cluster: Marten + PostgreSQL
- Realtime: SignalR (옵션 Redis backplane)
- Frontend: React + TypeScript + Vite

## 프로젝트 구조

- `Orleans_Demo/`: 웹 서버 + SignalR Hub + API
- `GrainInterfaces/`: Orleans grain contracts
- `GrainImplement/`: Orleans grain 구현
- `Orleans_Demo/frontend/`: React/Vite 프론트엔드
- `Orleans_Demo/wwwroot/`: 프론트 빌드 산출물 배포 경로

## 사전 준비

1. .NET SDK 10
2. Node.js 18+
3. PostgreSQL (Marten/Orleans 저장소)
4. (선택) Redis (SignalR backplane 사용 시)

## 설정

주요 설정 파일:

- `Orleans_Demo/appsettings.json`
- `Orleans_Demo/appsettings.Development.json`
- `Orleans_Demo/appsettings.Silo1.json` (필요 시 `Silo2~5`)

필수 확인 항목:

- `ConnectionStrings:MyDatabase` (PostgreSQL 연결 문자열)
- `SignalR:Redis:Enabled` 및 Redis 연결 문자열(옵션)

## 실행 방법

### 1) 프론트엔드 의존성 설치

```powershell
cd Orleans_Demo/frontend
npm install
```

### 2) 프론트엔드 빌드 (wwwroot로 출력)

```powershell
npm run build
```

### 3) 백엔드 실행

루트에서:

```powershell
dotnet run --project Orleans_Demo/Orleans_Demo.csproj
```

브라우저 접속:

- 기본: `http://127.0.0.1:5066`

## 개발 모드 (프론트 별도 실행)

프론트 개발 서버:

```powershell
cd Orleans_Demo/frontend
npm run dev
```

Vite 개발 서버는 `/api`, `/hubs`를 백엔드(`http://127.0.0.1:5066`)로 프록시합니다.

## 빌드

솔루션 빌드:

```powershell
dotnet build Orleans_Demo.slnx
```

## 주요 API

- `POST /api/chat/{roomId}/join`
- `POST /api/chat/{roomId}/leave`
- `POST /api/chat/{roomId}/leave-keepalive?userId=...`
- `POST /api/chat/{roomId}/message`
- `POST /api/chat/{roomId}/react`
- `GET /api/chat/{roomId}/messages?take=100`

## 비고

- 프론트는 빌드 후 `Orleans_Demo/wwwroot` 정적 파일로 서비스됩니다.
- 백엔드/프론트 병행 수정 시, UI 반영을 위해 `npm run build`를 다시 실행하세요.
