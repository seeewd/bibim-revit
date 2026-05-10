# Revit 2024 OpenRouter Guide Sweep Results

이 폴더는 2026-05-10에 실행한 BIBIM Revit 2024 OpenRouter 모델 스윕 결과를 깃에 올리기 좋게 정리한 패키지입니다.

원본 실행 출력은 `testoutput/guide-full-1x-r2024-20260510_150237`에 있으며, 해당 폴더는 RVT 복사본과 대량 로그를 포함하므로 `.gitignore` 대상입니다. 이 패키지에는 보고서, 요약 CSV, 재현 입력, 스크립트, 캡쳐 PNG만 포함했습니다.

## Included

| 경로 | 내용 |
|---|---|
| `reports/guide_sweep_report_ko.md` | 한국어 상세 보고서 |
| `reports/guide_sweep_report.md` | 영어 요약 보고서 |
| `csv/report_overall.csv` | 모델별 전체 지표 |
| `csv/report_metrics_by_lv.csv` | 난이도 LV별 지표 |
| `csv/report_failures.csv` | 실패 케이스 목록 |
| `csv/runtime_summary.csv` | Revit 런타임 실행 요약 |
| `inputs/bibim_llm_test_guide.md` | 테스트 가이드 원문 |
| `inputs/guide_all_prompts.json` | 실행에 사용한 15개 프롬프트 정의 |
| `inputs/guide_models_sweep.json` | 실행에 사용한 모델 목록 |
| `scripts/run_guide_runtime.ps1` | 런타임 검증 스크립트 |
| `scripts/revit_modal_autoclick.ps1` | Revit 모달 자동 처리 스크립트 |
| `screenshots/gemma4-revit2024-pilot/*.png` | 이전 Gemma4 Revit 2024 파일럿 캡쳐본 |

## Excluded

- OpenRouter API key: `key.txt`
- Revit 원본/복사 RVT, RFA 파일
- 모델별 전체 generation debug directory
- 모델별 전체 runtime output directory
- 빌드 산출물 `bin/`, `obj/`

## Main Result

- Claude Sonnet 4.6: Revit 런타임 15/15 성공
- Gemma4 26B A4B: 컴파일 15/15, Revit 런타임 14/15 성공
- Llama 3.3 70B: 컴파일 14/15, Revit 런타임 12/15 성공
- Codestral 2508: 컴파일 12/15, Revit 런타임 10/15 성공
- Qwen2.5-Coder 32B: planner JSON 계약 실패로 0/15
- Phi-4: OpenRouter tool-use 라우팅 실패로 0/15

자세한 해석은 `reports/guide_sweep_report_ko.md`를 확인하면 됩니다.
