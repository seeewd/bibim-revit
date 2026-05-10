# BIBIM 로컬 LLM 성능 테스트
**개발팀 지시서 · 테스트 프로토콜 · 결과 보고서 가이드**
v1.0 | 2026-05

---

## 1. 테스트 목적

이 테스트는 두 가지 핵심 정보를 수집하기 위한 것이다.

- **①** Claude Sonnet 4.6 기준 BIBIM 동작 품질에 근접하는 **최소 사양 로컬 LLM** 식별
- **②** 일반 건축/렌더링 사무소 PC 스펙에서 **실사용 가능한 최선의 LLM** 확인

이 결과는 향후 클라이언트 인터뷰에서 빠른 견적 및 로컬 LLM 도입 판단 기준으로 활용된다.

---

## 2. OpenRouter 연결 세팅

BIBIM은 이미 OpenAI 호환 Provider 구조를 갖추고 있다. OpenRouter는 동일한 API 형식을 사용하므로 Base URL과 키만 교체하면 된다.

### 2-1. ConfigService 수정

`rag_config.json`에 아래 필드 추가:

```json
"openrouter_api_key": "OPENROUTER_API_KEY_REDACTED",
"openrouter_base_url": "https://openrouter.ai/api/v1"
```

### 2-2. OpenAIProvider 수정

LlmProviderFactory에서 `openrouter_*` 키가 있을 경우 OpenAIProvider를 생성하되, HttpClient base address를 `openrouter_base_url`로 오버라이드한다.

모델 ID는 OpenRouter 형식 사용:
```
qwen/qwen-2.5-coder-32b-instruct
mistralai/codestral-2501
meta-llama/llama-3.3-70b-instruct
```

### 2-3. 주의 사항

- OpenRouter는 **Chat Completions API** 사용 (Responses API 아님)
- OpenAIProvider의 엔드포인트 분기 확인 필요
- HTTP Header에 아래 추가 권장 (OpenRouter 정책)

```
"HTTP-Referer": "https://bibim.app"
"X-Title": "BIBIM Revit Agent"
```

---

## 3. 테스트 대상 LLM 후보

아래 모델들을 OpenRouter를 통해 테스트한다. 로컬 실행 가능 모델은 VRAM 기준으로 선별했다.

| 모델 ID (OpenRouter) | 제공사 | 파라미터 | VRAM (4bit) | 비고 |
|---|---|---|---|---|
| `claude-sonnet-4-6` | Anthropic | – | – | ★★★★★ 기준선 (Baseline) |
| `qwen/qwen-2.5-coder-32b-instruct` | Alibaba | 32B | ≤20 GB | ★★★★☆ 24GB VRAM 최적 |
| `qwen/qwen-2.5-coder-14b-instruct` | Alibaba | 14B | ≤9 GB | ★★★☆☆ 16GB VRAM 최적 |
| `mistralai/codestral-2501` | Mistral | 22B | ≤14 GB | ★★★☆☆ C# 특화 |
| `qwen/qwen-2.5-coder-7b-instruct` | Alibaba | 7B | ≤5 GB | ★★☆☆☆ 하한선 검증용 |
| `meta-llama/llama-3.3-70b-instruct` | Meta | 70B | ≤42 GB | ★★★★☆ API 전용 고사양 |
| `microsoft/phi-4` | Microsoft | 14B | ≤9 GB | ★★★☆☆ 경량 효율성 검증 |

---

## 4. 건축/렌더링 PC 스펙 기준

일반 건축사무소 및 렌더링 스튜디오의 워크스테이션 스펙을 3등급으로 분류한다.

| PC 등급 | 하드웨어 기준 | 권장 모델 | 예상 평가 |
|---|---|---|---|
| 고사양 렌더팜 | RTX 4090 · 128GB RAM | Qwen2.5-Coder:32B | 적합 ✓ 실사용 가능 |
| 중간 워크스테이션 | RTX 4080 · 64GB RAM | Qwen2.5-Coder:14B / Codestral:22B | 제한적 ✓ LV3 이하 권장 |
| 보급형 | RTX 3080 · 32GB RAM | Qwen2.5-Coder:7B | 미흡 △ LV1~2만 안정 |

> VRAM 부족 시 CPU 오프로드 가능하나 속도 5~10배 저하. 실용성 없음으로 판정.

---

## 5. 측정 지표 정의

각 프롬프트 실행 시 아래 6가지 지표를 기록한다. 모든 테스트는 난이도별로 **3회 반복 후 평균값** 사용.

| 지표명 | 단위 | 정의 |
|---|---|---|
| 컴파일 성공률 | % | Roslyn 분석기 통과 여부 (pass/fail) |
| 첫 시도 성공률 | % | 재시도 없이 1회 만에 실행 성공 |
| 실행 성공률 | % | Revit에서 런타임 에러 없이 완료 |
| 평균 응답 시간 | 초 (s) | 프롬프트 입력 → 코드 생성 완료 |
| 평균 재시도 횟수 | 회 | Roslyn 실패로 인한 재생성 횟수 |
| API 오류율 | % | 존재하지 않는 Revit API 메서드 사용 비율 |

**기준선:** Claude Sonnet 4.6의 각 지표 값을 100%로 두고 상대 비율로 비교한다.

---

## 6. 테스트 프로토콜

### 6-1. 테스트 환경

- Revit 버전: 2025 또는 2026
- 샘플 프로젝트: 표준 사무소 모델 (층수 5개 이상, 룸 20개 이상, 커튼월 포함)
- 각 모델별 테스트 전 BIBIM 대화 초기화 (세션 리셋)
- 모든 테스트는 동일 프롬프트 텍스트, 동일 Revit 상태에서 실행

### 6-2. 실행 순서

1. 모델 설정 변경 → BIBIM 재시작
2. LV1부터 순서대로 프롬프트 입력
3. 각 실행마다 지표 기록 (결과 보고서 템플릿 사용)
4. 실패 시 최대 3회까지 자동 재시도 허용, 그 이상은 FAIL 기록
5. 전 LV 완료 후 다음 모델로 전환

### 6-3. FAIL 판정 기준

- Roslyn 컴파일 3회 실패
- Revit 실행 중 예외 (Exception) 발생
- 잘못된 결과 (파라미터 오기입, 엉뚱한 요소 선택 등)
- 응답 시간 180초 초과

---

## 7. 결과 보고서 템플릿

아래 표를 **LV별로 각각 작성**. 총 5개 표 (LV1~LV5).

| 모델 | 컴파일 성공률 | 첫 시도 성공률 | 실행 성공률 | 평균 응답(s) | API 오류율 |
|---|---|---|---|---|---|
| claude-sonnet-4-6 (기준) | | | | | |
| qwen-2.5-coder-32b | | | | | |
| qwen-2.5-coder-14b | | | | | |
| codestral-2501 | | | | | |
| qwen-2.5-coder-7b | | | | | |

**★ 권장 판정 기준 (내부 기준)**

- 컴파일 성공률 **90% 이상** → 실사용 가능
- 첫 시도 성공률 **70% 이상** → 양호
- 평균 응답 시간 **60초 이내** → 허용
- API 오류율 **10% 이하** → 안정

**최종 요약 섹션에 포함할 항목:**

- Sonnet 4.6 대비 70% 이상 성능 달성한 최소 모델 → **'최소 사양 권장 모델'**
- 렌더링 PC 등급별 추천 모델 매핑
- 각 LV에서 처음으로 FAIL이 나오는 모델 임계점

---

## 8. 난이도별 BIBIM 테스트 프롬프트

아래 프롬프트는 BIBIM 채팅창에 그대로 입력한다. 한국어로 입력, 영어 모델도 동일 텍스트 사용.

---

### 🟢 LV 1 — 조회 / Read-Only

Revit API 읽기 호출만 사용. 코드 구조 단순. 실패 시 모델 기본 C# 능력 부재로 판정.

**P1-A**
> 현재 프로젝트의 모든 레벨 이름과 높이(mm)를 목록으로 알려줘

→ 기대 동작: `FilteredElementCollector`로 Level 수집 후 `Elevation` 출력

**P1-B**
> 현재 활성 뷰에 있는 요소들을 카테고리별로 분류해서 개수를 알려줘

→ 기대 동작: `BuiltInCategory` 기준 그룹화 및 카운트

**P1-C**
> 선택된 요소의 모든 파라미터 이름과 값을 출력해줘

→ 기대 동작: `Element.Parameters` 순회 후 Name/Value 출력

---

### 🔵 LV 2 — 단순 수정 / Single Element Edit

단일 요소 파라미터 변경 또는 기본 선택 작업. Transaction 사용 여부 확인.

**P2-A**
> 선택된 벽의 'Comments' 파라미터를 '검토 필요'로 변경해줘

→ 기대 동작: `LookupParameter`로 파라미터 접근 후 `Set()`

**P2-B**
> 현재 뷰에 있는 모든 문(Door) 요소를 선택해줘

→ 기대 동작: `FilteredElementCollector` + `BuiltInCategory.OST_Doors`

**P2-C**
> 'RF' 라는 이름의 레벨을 높이 14000mm 위치에 새로 추가해줘

→ 기대 동작: `Level.Create()` API 사용

---

### 🟠 LV 3 — 다중 요소 / Multi-Element + Filtering

여러 요소를 순회하거나 조건 필터링 후 일괄 수정. 반복문 + Transaction 복합 사용.

**P3-A**
> 현재 뷰의 모든 창문(Window) 요소에 'Fire Rating' 파라미터를 '60min'으로 일괄 설정해줘

→ 기대 동작: Collector → 순회 → `SetValueString()`

**P3-B**
> 프로젝트 전체 벽(Wall) 타입별 총 길이(m)를 계산해서 알려줘

→ 기대 동작: `WallType` 그룹화 + `LocationCurve.Length` 합산

**P3-C**
> 면적이 20㎡ 이상인 모든 방(Room)을 선택해줘

→ 기대 동작: `Room.Area` 필터 + SelectionSet

---

### 🟣 LV 4 — 복합 로직 / Multi-Category + Calculation

두 개 이상 카테고리 교차 또는 계산 결과를 파라미터에 기입. 오류 처리 능력 확인.

**P4-A**
> 모든 방(Room)을 순회해서 'Department' 파라미터가 비어있는 방들을 '미분류'로 설정해줘

→ 기대 동작: Room 순회 + null 체크 + `SetValueString()`

**P4-B**
> 현재 뷰의 모든 기둥(Column)을 높이별로 그룹화하고 각 그룹의 개수와 평균 단면적을 알려줘

→ 기대 동작: `FamilyInstance` + `BuiltInParameter.INSTANCE_LENGTH_PARAM` 기반 그룹화

**P4-C**
> 선택된 커튼월의 패널 크기(너비×높이)를 분석해서 동일 크기별 수량을 정리해줘

→ 기대 동작: `CurtainGrid` → Panels → BoundingBox 분석

---

### 🔴 LV 5 — 고급 자동화 / Advanced Automation

복잡한 Revit API 조합, 다중 Transaction, 예외 처리. 모델의 실제 코드 이해도 상한선 측정.

**P5-A**
> 전체 프로젝트에서 층별로 내벽/외벽/커튼월 면적을 각각 계산하고, 각 레벨의 'Area Summary' 파라미터에 결과를 JSON 형식으로 저장해줘

→ 기대 동작: Level 순회 → 각 층 Wall/CurtainWall 필터 → Function 파라미터로 내/외벽 구분 → Area 합산 → `SetValueString()`

**P5-B**
> 선택된 그리드 라인들의 교점 좌표를 계산하고 각 교점에 'M_Concrete-Round-Column' 패밀리를 자동 배치해줘

→ 기대 동작: `Grid.Curve` 교점 계산 → FamilySymbol 로드 확인 → `NewFamilyInstance()` 반복 배치

**P5-C**
> 모든 방의 둘레 길이와 면적비율(둘레²÷면적)을 계산해서 'Compactness Ratio' 파라미터에 소수점 둘째 자리로 저장해줘

→ 기대 동작: `Room.Boundary` → 둘레 합산 → 비율 계산 → `SetValueDouble()`

---

## 9. 결과 활용 — 클라이언트 대화 가이드

테스트 완료 후 아래 시나리오별로 즉답 가능한 기준을 만든다.

### 시나리오 A — 클라우드 API 사용 가능 (Sonnet 4.6)

- LV1~5 모두 안정. 월 사용량 기준 견적 제시.
- 경량 작업 (LV1~2 위주) 예상 시 약 $10~30/월
- 코드 생성 집중 (LV4~5 포함) 시 약 $40~80/월

### 시나리오 B — 로컬 LLM 필요 (NDA / 보안)

- RTX 4090 보유 시: Qwen2.5-Coder:32B → LV___ 까지 통과 *(테스트 결과 기입)*
- RTX 4080 보유 시: Qwen2.5-Coder:14B → LV___ 까지 통과 *(테스트 결과 기입)*
- 그 이하 스펙: 로컬 운용 비권장, API 사용 또는 하드웨어 업그레이드 검토 권유

### 시나리오 C — 팀 단위 사용

- 로컬 인퍼런스 서버 구성 필요 (1대 서버 → 다수 클라이언트)
- 권장 스펙: A6000 48GB × 1 또는 4090 × 2 (NVLink 불가이므로 텐서 분산 설정 필요)
- 설정 지원 별도 협의

---

*BIBIM · Internal Use Only · 2026*
