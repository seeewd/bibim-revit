# BIBIM Revit 2024 LLM 모델 테스트 결과 보고서

## 1. 요약

이번 테스트는 `bibim_llm_test_guide.md`의 LV1-LV5 프롬프트 15개를 기준으로, Revit 2024 환경에서 여러 OpenRouter 모델이 BIBIM의 실제 작업 흐름을 얼마나 안정적으로 통과하는지 확인하기 위해 진행했다. 단순히 코드가 생성되는지만 본 것이 아니라, 컴파일 성공 여부와 Revit 프로세스 안에서의 실제 런타임 실행 결과까지 확인했다.

테스트 결과, 현재 플로우에서 가장 안정적인 모델은 `Claude Sonnet 4.6`이었다. 15개 프롬프트 모두 컴파일과 Revit 런타임 실행을 통과했다. `Gemma4 26B A4B`는 컴파일 15/15, 런타임 14/15로 가장 유망한 대체 후보였지만 응답 시간이 길고 LV5에서 1건의 실패가 있었다. `Llama 3.3 70B`는 중간 난이도까지는 쓸 만했으나 LV3 이상에서 런타임 오류가 나타났다. `Codestral 2508`은 빠르지만 LV3 이후 안정성이 떨어졌고, `Qwen2.5-Coder 32B`와 `Phi-4`는 현재 BIBIM 앱 플로우에서는 바로 사용하기 어렵다.

이 결과는 모델별 1회 실행 결과이므로 최종 벤치마크로 확정하기보다는, 정식 3회 반복 테스트 전에 후보 모델을 걸러내는 파일럿 스윕으로 보는 것이 맞다.

## 2. 테스트 목적

이번 테스트의 목적은 다음과 같다.

- Revit 2024에서 BIBIM 자동 테스트 플로우가 정상적으로 동작하는지 확인
- OpenRouter 잔고 및 API 키 변경 이후 모델 호출이 정상적으로 되는지 확인
- `bibim_llm_test_guide.md` 기준으로 LV1-LV5 작업을 여러 모델에 동일하게 수행
- 코드 생성 성공뿐 아니라 Revit 내부 런타임 실행 성공률까지 측정
- 정식 3회 반복 벤치마크에 올릴 모델과 제외할 모델을 선별
- 실패 로그를 기반으로 다음 테스트 전에 수정해야 할 프롬프트/런타임 가드레일을 도출

## 3. 테스트 환경

| 항목 | 내용 |
|---|---|
| 테스트 일자 | 2026-05-10 |
| Revit 버전 | Autodesk Revit 2024 |
| 테스트 모델 | Snowdon Towers Sample Architectural.rvt |
| 기준 가이드 | `C:\Users\cho\bibim-revit\bibim_llm_test_guide.md` |
| OpenRouter 키 파일 | `C:\Users\cho\bibim-revit\key.txt` |
| 결과 폴더 | `C:\Users\cho\bibim-revit\testoutput\guide-full-1x-r2024-20260510_150237` |
| 반복 수 | 모델별 1회 |
| 런타임 검증 방식 | 컴파일 성공 케이스만 Revit 2024에서 실제 실행 |
| Revit 실행 방식 | 프롬프트마다 새 Revit 프로세스와 복사된 RVT 파일 사용 |

## 4. 테스트 전 처리한 사항

Revit 2024에서 테스트를 안정적으로 돌리기 위해 다음 처리를 먼저 했다.

- `Bibim.RevitAutoSmoke`를 Revit 2024가 로드할 수 있도록 `net48` 타깃으로 빌드 가능하게 수정했다.
- OpenRouter provider를 Anthropic Messages API 방식이 아니라 OpenAI 호환 `chat/completions` 방식으로 맞췄다.
- Revit 시작 시 뜨는 “서명 없는 애드인” 팝업은 외부 자동 클릭 스크립트로 “항상 로드”되도록 처리했다.
- Revit 프로젝트 열기 중 “해결되지 않은 참조” 팝업은 AutoSmoke 내부 `DialogBoxShowing` 처리로 “무시하고 계속 프로젝트 열기”되도록 처리했다.
- 각 테스트가 서로 영향을 주지 않도록 프롬프트별로 새 문서 복사본과 새 Revit 프로세스를 사용했다.

## 5. 테스트 모델

| 표시 이름 | 실제 OpenRouter 모델 ID | 비고 |
|---|---|---|
| Claude Sonnet 4.6 기준 | `anthropic/claude-sonnet-4.6` | 기준 모델 |
| Qwen2.5-Coder 32B | `qwen/qwen-2.5-coder-32b-instruct` | planner JSON 실패 |
| Codestral 2508 | `mistralai/codestral-2508` | `codestral-2501` 대체 |
| Llama 3.3 70B | `meta-llama/llama-3.3-70b-instruct` | 고성능 오픈 모델 후보 |
| Phi-4 | `microsoft/phi-4` | OpenRouter tool-use 라우팅 실패 |
| Gemma4 26B A4B | `google/gemma-4-26b-a4b-it` | 이번 Revit 2024 파일럿 요청 모델 |

OpenRouter에서 가이드에 있던 일부 모델 ID는 정확히 매칭되지 않았다. `mistralai/codestral-2501`, `qwen/qwen-2.5-coder-14b-instruct`, `qwen/qwen-2.5-coder-7b-instruct`는 정확한 사용 가능 ID를 찾지 못해 이번 스윕에서는 제외했다.

## 6. 지표 정의

| 지표 | 의미 |
|---|---|
| 컴파일 성공 | 모델이 생성한 C# 코드가 Roslyn 검증 또는 최종 컴파일을 통과했는지 |
| 최초 시도 성공 | 코드 수정/재컴파일 재시도 없이 통과한 것으로 볼 수 있는 성공률 |
| Revit 런타임 성공 | 실제 Revit 2024 프로세스 안에서 실행까지 성공했는지 |
| 평균 응답 시간 | planner, RAG/tool 호출, codegen을 포함한 평균 모델 응답 시간 |
| API/코드 오류 | 생성된 코드의 Revit API 사용 문제 또는 코드 오류로 분류되는 실패 |

주의할 점은 `Qwen2.5-Coder 32B`와 `Phi-4`의 실패가 API/코드 오류율에는 잘 드러나지 않는다는 점이다. Qwen은 코드 생성 전 planner JSON 계약에서 실패했고, Phi-4는 OpenRouter provider/tool-use 라우팅 단계에서 실패했기 때문에 코드 품질 평가 단계까지 도달하지 못했다.

## 7. 전체 결과

| 모델 | 컴파일 성공 | 최초 시도 성공 | Revit 런타임 성공 | 평균 응답 시간 | API/코드 오류 |
|---|---:|---:|---:|---:|---:|
| Claude Sonnet 4.6 기준 | 15/15 (100%) | 6/15 (40%) | 15/15 (100%) | 43.5초 | 0/15 (0%) |
| Qwen2.5-Coder 32B | 0/15 (0%) | 0/15 (0%) | 0/15 (0%) | 10.2초 | 0/15 (0%) |
| Codestral 2508 | 12/15 (80%) | 6/15 (40%) | 10/15 (66.7%) | 14.4초 | 5/15 (33.3%) |
| Llama 3.3 70B | 14/15 (93.3%) | 5/15 (33.3%) | 12/15 (80%) | 22.7초 | 3/15 (20%) |
| Phi-4 | 0/15 (0%) | 0/15 (0%) | 0/15 (0%) | 10.2초 | 0/15 (0%) |
| Gemma4 26B A4B | 15/15 (100%) | 9/15 (60%) | 14/15 (93.3%) | 85.6초 | 1/15 (6.7%) |

전체 결과만 보면 Claude가 가장 안정적이고, Gemma4가 가장 가까운 대체 후보로 보인다. 다만 Gemma4는 응답 시간이 Claude보다 길고 LV5에서 1건의 런타임 컴파일 실패가 있었다. Llama는 컴파일 성공률은 높지만 실제 실행 성공률이 떨어졌고, Codestral은 빠른 대신 LV3 이상에서 실패가 늘었다.

## 8. 난이도별 결과

### LV1 결과

| 모델 | 컴파일 성공률 | 최초 시도 성공률 | 런타임 성공률 | 평균 응답 시간 | API/코드 오류 |
|---|---:|---:|---:|---:|---:|
| Claude Sonnet 4.6 기준 | 100% | 100% | 100% | 18.6초 | 0% |
| Qwen2.5-Coder 32B | 0% | 0% | 0% | 9.8초 | 0% |
| Codestral 2508 | 100% | 66.7% | 100% | 5.3초 | 0% |
| Llama 3.3 70B | 100% | 33.3% | 100% | 15.6초 | 0% |
| Phi-4 | 0% | 0% | 0% | 6.2초 | 0% |
| Gemma4 26B A4B | 100% | 100% | 100% | 40.7초 | 0% |

LV1은 읽기 중심의 기본 작업이다. Claude, Gemma4, Codestral, Llama는 모두 실제 실행까지 성공했다. Qwen과 Phi-4는 코드 품질 문제가 아니라 앱 플로우 진입 단계에서 막혔다.

### LV2 결과

| 모델 | 컴파일 성공률 | 최초 시도 성공률 | 런타임 성공률 | 평균 응답 시간 | API/코드 오류 |
|---|---:|---:|---:|---:|---:|
| Claude Sonnet 4.6 기준 | 100% | 100% | 100% | 13.5초 | 0% |
| Qwen2.5-Coder 32B | 0% | 0% | 0% | 6.1초 | 0% |
| Codestral 2508 | 100% | 66.7% | 100% | 3.5초 | 0% |
| Llama 3.3 70B | 100% | 33.3% | 100% | 9.2초 | 0% |
| Phi-4 | 0% | 0% | 0% | 8.0초 | 0% |
| Gemma4 26B A4B | 100% | 100% | 100% | 41.4초 | 0% |

LV2는 선택 요소 수정, 활성 뷰 기준 선택, 레벨 생성처럼 간단한 쓰기 작업을 포함한다. Claude와 Gemma4는 안정적이었고, Codestral도 LV2까지는 빠르고 쓸 만했다.

### LV3 결과

| 모델 | 컴파일 성공률 | 최초 시도 성공률 | 런타임 성공률 | 평균 응답 시간 | API/코드 오류 |
|---|---:|---:|---:|---:|---:|
| Claude Sonnet 4.6 기준 | 100% | 0% | 100% | 33.3초 | 0% |
| Qwen2.5-Coder 32B | 0% | 0% | 0% | 6.3초 | 0% |
| Codestral 2508 | 66.7% | 33.3% | 33.3% | 13.6초 | 66.7% |
| Llama 3.3 70B | 100% | 66.7% | 66.7% | 6.7초 | 33.3% |
| Phi-4 | 0% | 0% | 0% | 12.8초 | 0% |
| Gemma4 26B A4B | 100% | 33.3% | 100% | 41.4초 | 0% |

LV3부터 모델 간 차이가 크게 벌어졌다. Codestral은 벽 타입 길이 집계에서 `FamilyInstance`를 `Wall`로 직접 캐스팅하는 오류가 발생했고, 룸 면적 필터링 작업에서는 컴파일 실패가 발생했다. Llama도 벽 타입 집계에서 `NullReferenceException`이 발생했다. Claude와 Gemma4는 런타임 기준으로 모두 통과했다.

### LV4 결과

| 모델 | 컴파일 성공률 | 최초 시도 성공률 | 런타임 성공률 | 평균 응답 시간 | API/코드 오류 |
|---|---:|---:|---:|---:|---:|
| Claude Sonnet 4.6 기준 | 100% | 0% | 100% | 58.7초 | 0% |
| Qwen2.5-Coder 32B | 0% | 0% | 0% | 13.5초 | 0% |
| Codestral 2508 | 33.3% | 33.3% | 33.3% | 34.6초 | 66.7% |
| Llama 3.3 70B | 66.7% | 33.3% | 66.7% | 44.4초 | 33.3% |
| Phi-4 | 0% | 0% | 0% | 10.1초 | 0% |
| Gemma4 26B A4B | 100% | 66.7% | 100% | 189.7초 | 0% |

LV4는 커튼월 패널, 기둥 높이 그룹, 룸 파라미터 수정처럼 Revit API 세부 지식이 필요한 작업이다. Claude와 Gemma4는 런타임까지 성공했지만, Gemma4의 평균 응답 시간은 189.7초로 크게 늘었다. Codestral은 구조 단면 관련 API 사용과 커튼월 패널 접근에서 컴파일 오류가 발생했다. Llama는 선택된 커튼월 패널 작업에서 `Element`를 `CurtainGrid`로 잘못 패턴 매칭하려다 컴파일에 실패했다.

### LV5 결과

| 모델 | 컴파일 성공률 | 최초 시도 성공률 | 런타임 성공률 | 평균 응답 시간 | API/코드 오류 |
|---|---:|---:|---:|---:|---:|
| Claude Sonnet 4.6 기준 | 100% | 0% | 100% | 93.4초 | 0% |
| Qwen2.5-Coder 32B | 0% | 0% | 0% | 15.2초 | 0% |
| Codestral 2508 | 100% | 0% | 66.7% | 15.2초 | 33.3% |
| Llama 3.3 70B | 100% | 0% | 66.7% | 37.3초 | 33.3% |
| Phi-4 | 0% | 0% | 0% | 13.8초 | 0% |
| Gemma4 26B A4B | 100% | 0% | 66.7% | 115.0초 | 33.3% |

LV5는 복합 집계와 배치 작업이다. Claude만 3개 작업을 모두 런타임까지 통과했다. Gemma4는 컴파일 자체는 모두 성공했지만, 레벨별 벽 면적 요약 작업에서 생성 코드가 `System.Text.Json`을 사용해 Revit 2024 런타임 컴파일에서 실패했다. Llama는 같은 유형의 작업에서 `Level.Id`를 뷰 ID처럼 사용해 `viewId is not a view` 예외가 발생했다. Codestral은 벽 카테고리에서 `FaceWall`을 `Wall`로 직접 캐스팅해 실패했다.

## 9. 모델별 평가

### Claude Sonnet 4.6 기준

Claude는 이번 테스트의 기준 모델로 가장 안정적이었다. 전체 15개 프롬프트 모두 컴파일과 Revit 런타임 실행을 통과했다. LV3 이상에서는 최초 시도 성공률이 낮아져 내부 재검증/수정 흐름에 의존한 것으로 보이지만, 최종 성공률은 가장 좋았다.

결론적으로 현재 BIBIM 앱 플로우에서 품질 기준선을 잡는 모델로 적합하다. 단점은 평균 응답 시간이 43.5초로 빠른 편은 아니라는 점이다.

### Gemma4 26B A4B

Gemma4는 이번 테스트에서 가장 유망한 비-Claude 후보였다. 전체 15개 프롬프트가 모두 컴파일을 통과했고, Revit 런타임도 14/15로 높았다. 실패한 LV5-A도 Revit API 이해 부족보다는 `System.Text.Json` 참조 문제에 가까워, 시스템 프롬프트 가드레일로 개선 가능성이 있다.

단점은 속도다. 전체 평균 응답 시간이 85.6초였고, LV4에서는 평균 189.7초까지 늘었다. 실사용 후보로 보려면 성공률뿐 아니라 대기 시간을 줄일 수 있는지 추가 확인이 필요하다.

### Llama 3.3 70B

Llama는 컴파일 성공률이 14/15로 높고 LV1-LV2에서는 안정적이었다. 그러나 LV3 이상에서 벽 타입 집계, 커튼월 패널, 레벨별 집계 같은 API 세부 작업에서 오류가 나타났다.

Claude나 Gemma4보다 빠른 구간도 있었지만, 실제 Revit 런타임 안정성은 12/15로 떨어졌다. 고성능 후보로는 남겨둘 수 있으나, Revit API 가드레일 보강 후 재테스트가 필요하다.

### Codestral 2508

Codestral은 LV1-LV2에서 매우 빠르고 성공률도 좋았다. 특히 LV2 평균 응답 시간이 3.5초로 가장 빨랐다. 하지만 LV3부터 캐스팅 오류와 컴파일 오류가 증가했고, LV4에서는 런타임 성공률이 33.3%까지 떨어졌다.

따라서 간단한 읽기/선택/수정 작업의 빠른 후보로는 의미가 있지만, 복합 Revit 자동화 모델로 바로 쓰기에는 위험하다.

### Qwen2.5-Coder 32B

Qwen은 이번 테스트에서 코드 생성 품질을 평가할 단계까지 가지 못했다. BIBIM의 task planner가 기대하는 JSON 형식을 지키지 못하거나 `shouldAutoRun` 주변에서 JSON parse 오류가 발생했다.

즉, 이 결과는 “Revit API 코드를 못 짠다”보다는 “현재 BIBIM planner 계약과 맞지 않는다”로 해석해야 한다. 이 모델을 다시 보려면 planner 프롬프트를 모델별로 단순화하거나, planner를 우회한 codegen-only 테스트를 별도로 해야 한다.

### Phi-4

Phi-4는 대부분의 프롬프트에서 OpenRouter가 tool-use endpoint를 제공하지 않아 실패했다. 오류 메시지는 `No endpoints found that support tool use`였고, `search_revit_api`를 비활성화하거나 tool-use 없는 경로로 라우팅해야 한다는 내용이었다.

현재 BIBIM 플로우가 `search_revit_api`, `run_roslyn_check` 등 도구 호출을 전제로 하기 때문에, Phi-4는 그대로는 테스트 대상에서 제외하는 것이 맞다.

## 10. 주요 실패 원인 정리

| 모델 | 실패 유형 | 대표 프롬프트 | 원인 |
|---|---|---|---|
| Qwen2.5-Coder 32B | planner JSON 실패 | 전체 | task planner 응답이 JSON 계약을 지키지 못함 |
| Phi-4 | OpenRouter tool-use 라우팅 실패 | 대부분 | 해당 provider endpoint가 tool-use를 지원하지 않음 |
| Codestral 2508 | Revit API 캐스팅 오류 | LV3-B, LV5-A | `FamilyInstance` 또는 `FaceWall`을 `Wall`로 직접 캐스팅 |
| Codestral 2508 | 컴파일 오류 | LV3-C, LV4-B, LV4-C | 정의되지 않은 변수, 잘못된 API 멤버, `object.GetPanelIds()` 호출 |
| Llama 3.3 70B | 런타임 예외 | LV3-B | null 체크 부족으로 `NullReferenceException` 발생 |
| Llama 3.3 70B | 잘못된 API 사용 | LV5-A | `Level.Id`를 view-scoped collector의 view id로 사용 |
| Gemma4 26B A4B | 런타임 컴파일 실패 | LV5-A | 생성 코드가 `System.Text.Json`을 사용했으나 Revit 2024 런타임 컴파일 참조에 없음 |

## 11. 테스트 후 반영한 수정

이번 결과를 보고 다음 테스트 전에 바로 줄일 수 있는 실패 패턴은 시스템 프롬프트에 반영했다.

- Revit 2024 런타임 컴파일 코드에서 `System.Text.Json`을 사용하지 않도록 제한했다.
- JSON 문자열이 꼭 필요할 경우 `Newtonsoft.Json.JsonConvert.SerializeObject(...)`를 쓰도록 안내했다.
- 벽 요소 수집 시 `.OfCategory(BuiltInCategory.OST_Walls)` 결과를 무조건 `Wall`로 캐스팅하지 않도록 제한했다.
- 벽 작업은 `.OfClass(typeof(Wall))` 기반 수집을 우선하도록 안내했다.
- 선택된 커튼월 작업에서는 선택 요소를 `Wall`로 확인한 뒤 `wall.CurtainGrid`를 사용하도록 안내했다.
- `Level.Id`를 `FilteredElementCollector(doc, viewId)`의 view id로 잘못 사용하는 패턴을 금지했다.

수정 위치:

- `C:\Users\cho\bibim-revit\Bibim.Core\Services\Prompts\CodeGenSystemPrompt.cs`

빌드 확인:

- `dotnet build Bibim.RevitAutoSmoke\Bibim.RevitAutoSmoke.csproj -c Debug -f net48 /p:RevitSdkPath="C:\Program Files\Autodesk\Revit 2024"`
- 결과: 오류 0개, 경고 0개

## 12. 캡쳐본 위치

이번 전체 스윕 폴더인 `guide-full-1x-r2024-20260510_150237`에는 별도 PNG 캡쳐본이 생성되지 않았다. 이번 전체 스윕의 프롬프트 세트는 코드 생성, 컴파일, Revit 런타임 검증 중심으로 실행되었고, 각 케이스별 출력 폴더에는 주로 결과 JSON, 컴파일 코드, RVT 복사본이 남았다.

이전에 Gemma4 Revit 2024로 “뷰 캡쳐” 테스트를 했을 때 생성된 캡쳐본은 아래 위치에 있다.

| 파일 | 위치 |
|---|---|
| 원본 뷰 캡쳐 | `C:\Users\cho\bibim-revit\testoutput\gemma4-revit2024-test-20260510_1340\runtime\google_gemma-4-26b-a4b-it__rfa_model_curve_crosshair\active_view - 3D 뷰 - {3D}.png` |
| 주석 포함 캡쳐 | `C:\Users\cho\bibim-revit\testoutput\gemma4-revit2024-test-20260510_1340\runtime\google_gemma-4-26b-a4b-it__rfa_model_curve_crosshair\active_view_annotated.png` |

더 이전 테스트 폴더에도 같은 유형의 캡쳐가 있다.

- `C:\Users\cho\bibim-revit\testoutput\google_gemma-4-26b-a4b-it\runtime\google_gemma-4-26b-a4b-it__rfa_model_curve_crosshair\active_view - 3D 뷰 - {3D}.png`
- `C:\Users\cho\bibim-revit\testoutput\google_gemma-4-26b-a4b-it\runtime\google_gemma-4-26b-a4b-it__rfa_model_curve_crosshair\active_view_annotated.png`

## 13. 산출물 목록

| 산출물 | 위치 |
|---|---|
| 영어 원본 보고서 | `C:\Users\cho\bibim-revit\testoutput\guide-full-1x-r2024-20260510_150237\guide_sweep_report.md` |
| 한국어 보고서 | `C:\Users\cho\bibim-revit\testoutput\guide-full-1x-r2024-20260510_150237\guide_sweep_report_ko.md` |
| 전체 모델 요약 CSV | `C:\Users\cho\bibim-revit\testoutput\guide-full-1x-r2024-20260510_150237\report_overall.csv` |
| 레벨별 지표 CSV | `C:\Users\cho\bibim-revit\testoutput\guide-full-1x-r2024-20260510_150237\report_metrics_by_lv.csv` |
| 실패 목록 CSV | `C:\Users\cho\bibim-revit\testoutput\guide-full-1x-r2024-20260510_150237\report_failures.csv` |
| 런타임 실행 요약 CSV | `C:\Users\cho\bibim-revit\testoutput\guide-full-1x-r2024-20260510_150237\runtime\runtime_summary.csv` |
| 모델별 생성 로그 | `C:\Users\cho\bibim-revit\testoutput\guide-full-1x-r2024-20260510_150237\generation` |
| 모델별 Revit 런타임 로그 | `C:\Users\cho\bibim-revit\testoutput\guide-full-1x-r2024-20260510_150237\runtime` |

## 14. 한계

이번 테스트는 1회 스윕이기 때문에 모델의 평균적인 안정성을 확정하기에는 부족하다. 특히 LLM 응답은 같은 프롬프트에서도 변동이 있을 수 있으므로, 정식 비교를 위해서는 최소 3회 반복 평균이 필요하다.

또한 Qwen과 Phi-4의 실패는 생성 코드 품질 실패라기보다 앱 플로우와 provider 기능 지원 문제에 가깝다. 두 모델을 공정하게 비교하려면 planner/tool-use를 우회하거나 모델별 provider 설정을 따로 만든 별도 테스트가 필요하다.

마지막으로 이번 전체 스윕은 뷰 이미지 캡쳐 검증을 포함하지 않았다. 캡쳐 품질까지 평가하려면 캡쳐 전용 프롬프트를 테스트 세트에 포함하고, PNG 산출 여부와 이미지 내용을 별도로 확인해야 한다.

## 15. 다음 단계 제안

정식 결과를 만들려면 다음 순서가 적절하다.

1. Claude Sonnet 4.6, Gemma4 26B A4B, Llama 3.3 70B를 대상으로 3회 반복 테스트를 수행한다.
2. Codestral 2508은 전체 LV1-LV5가 아니라 LV1-LV3까지만 제한 테스트한다.
3. Qwen2.5-Coder 32B는 planner JSON 프롬프트를 단순화하거나 codegen-only 경로를 만든 뒤 재평가한다.
4. Phi-4는 tool-use를 끄는 별도 provider 설정이 생기기 전까지 정식 후보에서 제외한다.
5. 캡쳐 테스트가 중요하면 다음 스윕에 “뷰 캡쳐 생성 및 PNG 검증” 프롬프트를 별도 항목으로 추가한다.
6. 이번에 추가한 프롬프트 가드레일이 Gemma4 LV5-A, Llama LV5-A, Codestral 벽 캐스팅 실패를 줄이는지 재검증한다.

현재 기준의 추천은 다음과 같다.

- 운영 기준 모델: Claude Sonnet 4.6
- 대체 후보 1순위: Gemma4 26B A4B
- 추가 검증 후보: Llama 3.3 70B
- 제한적 사용 후보: Codestral 2508
- 현재 플로우 제외: Qwen2.5-Coder 32B, Phi-4
