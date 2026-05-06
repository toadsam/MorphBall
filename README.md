# MorphBall

Unity 6 기반 변신 구슬 게임 프로토타입입니다. 구슬 형태의 캐릭터 또는 오브젝트 변형 메커니즘을 실험하기 위한 3D 프로젝트입니다.

## 프로젝트 개요

`MorphBall`은 Unity 6 환경에서 변신/구슬 콘셉트의 게임 플레이를 실험하기 위한 저장소입니다. 현재는 3D 월드 에셋과 기본 프로젝트 구성이 중심이며, 이후 이동, 변신, 상호작용 시스템을 확장할 수 있는 기반 프로젝트입니다.

## 기술 스택

- Unity `6000.0.41f1`
- C#
- Unity Input System
- Unity UI
- Unity Timeline
- Visual Scripting

## 현재 구성

- `Assets/`: 게임 에셋과 씬 구성
- `Packages/`: Unity 패키지 의존성
- `ProjectSettings/`: Unity 프로젝트 설정
- `KUBIKOS - World`: 3D 월드 에셋 관련 파일

## 폴더 구조

```text
.
├── Assets/
├── Packages/
├── ProjectSettings/
└── README.md
```

## 실행 방법

1. Unity Hub에서 `6000.0.41f1` 버전을 설치합니다.
2. 저장소 루트 폴더를 Unity 프로젝트로 엽니다.
3. Unity가 패키지와 에셋을 임포트할 때까지 기다립니다.
4. `Assets` 아래의 씬을 열고 Play 버튼으로 실행합니다.

## 개발 방향 제안

- 구슬 이동 조작 구현
- 변신 상태별 능력 분리
- 카메라 추적 방식 정리
- 월드 오브젝트와 상호작용 추가
- 상태 전환 UI 또는 이펙트 추가

## README 확장 항목

기능이 추가되면 아래 내용을 보강하면 좋습니다.

- 게임 목표
- 조작법
- 시작 씬
- 구현된 변신 종류
- 플레이 영상 또는 빌드 링크
