# WP-04 scheduler and ICMP acceptance matrix

Status: Frozen design acceptance plan
Date: 2026-08-14 (Asia/Bangkok)

All cases use a fake monotonic clock and injectable fake ICMP transport. No test contacts a public IP address or a real network.

| ID | Scenario | Required evidence |
| --- | --- | --- |
| SCH-01 | Same installation/Probe/version computes the same jitter; distinct eligible inputs remain bounded in interval. | Deterministic fixture equality and offset range assertion. |
| SCH-02 | Wall-clock moves forward/backward. | Monotonic cadence unchanged. |
| SCH-03 | Suspend/resume or delayed scheduler crosses slots. | Only next future slot runs; missed slots counted; no catch-up burst. |
| SCH-04 | Same Probe is due while running. | One transport invocation; non-overlap guard holds. |
| CON-01 | Global capacity reaches 64/default and 1/256 boundaries. | Never exceed configured global permits; bounded admission/queue depth. |
| CON-02 | Same normalized target reaches per-target 1/default and 1/8 boundaries. | Never exceed target permits; permit release is reverse order on all exits. |
| CFG-01 | Applied snapshot has IPv4 enabled Probe inside both scopes. | One schedule created with expected interval/attempt settings. |
| CFG-02 | Hostname, invalid IPv4, disabled Probe, or out-of-ceiling target. | Snapshot rejected or unscheduled as applicable; transport never invoked; LKG retained. |
| ICMP-01 | Sequential success attempts with varied RTT. | Correct totals, zero loss, min/average/max, immutable result. |
| ICMP-02 | Mixed success/failure attempts. | Correct success total, ratio, RTT aggregates, fixed outcome category. |
| ICMP-03 | Timeout, unreachable, permission, adapter unavailable, invalid target, unknown platform error. | Deterministic mapping to the seven fixed categories. |
| LIFE-01 | Cancellation before admission, during delay, and during transport. | No target-failure result; permits released; lifecycle stops cleanly. |
| LIFE-02 | Graceful shutdown and revocation. | New admissions stop, active work cancels/drains within policy, no leaked locks. |
| OBS-01 | Logs/metrics with generated canaries and many Probe/target values. | No secret/raw configuration/raw target leak; no Probe/target labels; bounded label set. |
| HEALTH-01 | Target failure versus scheduler failure, invalid applied config, unusable transport. | Target failure leaves host healthy; systemic failures degrade/unhealthy. |
| BND-01 | Continuous due work beyond capacity. | Queue/admission remains bounded; skipped-run and lag metrics increase; no replay. |

Acceptance requires Agent B test evidence, Agent D security/concurrency review, Agent A confirmation of no backend/API/migration impact, Agent C no-impact confirmation, and Lead acceptance. Real ICMP evidence is separately blocked by UA-03; Windows Service operational evidence is separately blocked by UA-04.
