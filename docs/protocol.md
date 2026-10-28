# Protocol v3 — camera-primary torso events, independent optical backup

UTF-8 JSON, camelCase, microsecond integer clocks. HTTP8765 supplies setup/CA; HTTPS/WSS8766 supplies phone, pairing and authenticated APIs. Pair/state/health advertise protocol3. v2 clients are rejected by the phone. Legacy result endpoints cannot write any heat-owned runner's official time. Group administration and source selection are local WPF operations, never LAN admin endpoints.

New Heat.finishAuthority is camera-primary; missing historical JSON fields default to timy-device. requireRecordingBeforeStart:false retains manual camera operation. C0/C0M records common start; C1/C1M forms an independent one-to-one backup queue. Hardware backup elapsed uses device-time difference. Only explicit source selection with reason changes official source to photocell-backup. Video, optical and review revisions are independent. All binding/source operations snapshot optical bindings, visual bindings and selected sources in the transaction log.

## Capture and timestamps

RecordingReady adds captureFingerprint and frozen VisionSettings {lineX,top,bottom,left,right,direction,fixedSide}. Fingerprint contains phone device ID, UA, camera ID, actual dimensions/fps, codec, model and frame adapter implementation. A recording retry cannot mutate the fingerprint/settings. Original metadata, actual variable PTS and first preview stamp are diagnostics; recorder-start+PTS never computes primary elapsed.

Phone loads bundled BodyPix2.2.1 MobileNet .75, stride8, quant2, TFJS4.22/WASM. Worker uses torso_front/torso_back (official IDs12/13), largest connected torso mask, conservative multi-target association and a following frame. Pixel bytes are copied synchronously with rVFC metadata before inference. Prefer captureTime if available, else explicitly identify rvfc-presentationTime as requiring calibration. Latest-ready inference has at most one submitted frame: while Worker is busy, recording continues and analysis samples the next fresh frame when ready. Analysis input has a 320px maximum edge; full-resolution originals are retained. Tracker retains crossing-neighbor frames; sparse analysis intervals remain pending through frame-gap gates. Every emitted track is latched. Ambiguous reidentification cannot auto-confirm.

WebSocket sync {id,t1} returns {id,t1,t2,t3,sessionId,proof}. Proof is HMAC-SHA256 over session/device/id/t1/t2/t3 using the server-only pairing secret. Phone appends t4 and stores signed sample windows with immutable frame evidence; server checks signature, clock session and fit. Low RTT is a runtime condition only.

## Visual API

POST /api/vision-events accepts VisionSubmission: requestId,eventId,heatId,recordingId,deviceId,trackId,modelVersion,captureFingerprint,settings,clock,beforeMediaUs,afterMediaUs,maxFrameGapUs,confidence,ambiguous,issues[],frameHashes[3]. clock includes sessionId,pageClockId,mapId,source,mediaUs,clientUs,callbackClientUs,samples[{id,t1,t2,t3,t4,proof}]. Processing completion and arrival times are excluded. Immutable retries are idempotent, one crossing per recording/track, all owners must match.

POST /api/vision-events/{id}/correspondence accepts requestId,eventId,beforePtsUs,framePtsUs,afterPtsUs,maxHashDistance,uniquenessMargin,hashes[3],recordingSha256. Phone decodes actual raw video frames, compares 256-bit pixel difference hashes near a diagnostic search window, requires unique matches and ordered three frames. Search-window estimate never sets finish time. Server verifies original receipt SHA256, ownership, order, hash distances and margins; ambiguous/missing correspondence remains pending. Correspondence reports and changes append history.

GET/POST /api/heat-evidence/{id}/{kind}?heatId=...&offset=...&length=...&sha256=... retains resumable verified uploads. Visual event kinds: image,before,after,mask,metadata. Original uses recordingId/video and is shared once. Upload screenshot/mask metadata before original. Phone retains every source file. Five receipts alone cannot confirm; original correspondence and validated profile must also pass. Existing /api/reviews adds visionEventId; a manual frame change invalidates primary approval if selected PTS lacks matching clock evidence. It cannot overwrite device backup.

## Approval

Candidate estimate = mapped frame client monotonic time − start USB callback host time + measured USB delay correction − measured camera delay correction. Timy hardware start is retained separately. Signed round trips plus affine drift fit supply mapping; wrong session, poor RTT/jitter/age, ambiguous identity, missing frame, poor confidence, incomplete evidence or no matching profile all prevent approval. Primary score is confirmed only after background evidence/frame checks. If already explicitly using backup, later evidence does not silently switch source.

CameraValidationProfile binds rig/capture/model/clock/settings, correction parameters, runtime limits, reference method, reviewer and ≥100 traceable actual comparisons. Max absolute error≤10000us, reference uncertainty≤1000us, unique measurement IDs and normal/congested/long/frame-gap conditions required. No profiles ship. Import is local, append-only and versioned by new profile ID. The operator must substantiate actual measurement truth; software validates reported numeric conditions, not physical truth.

OfficialSource camera requires full eligibility; photocell-backup requires matched verified device event plus explicit reason. Undo of an adopted backup requires renewed source review. Revisions preserve past scores and source. Ranking uses official confirmed results only and heat rounding, excludes simulation and legacy estimates.

SQLite schema3 stores nested visual events with heats and validation_profiles plus existing runs/evidence/audits. Migration from1/2 uses SQLite backup API to pre-v3 snapshot before mutation, preserving historical timingMethod and default timy-device authority. Phone IndexedDB schema2 adds visual outbox while preserving schema1 clips/chunks/reviews. Host restart creates a fresh clock epoch: pending old-session candidates cannot be silently mapped into it; accepted historical revisions remain stored.


## 0.4.1 diagnostics and explicit pending closure

Authenticated WSS visionStatus carries heatId,recordingId,status,captured,analyzed,people,torsoPixels,queued,generated,saved,lastError. The host checks ownership and displays recent per-group diagnostics. It is operational status, not timing evidence. Ending a group locally can explicitly close with pending records: original unmatched events remain unassigned, existing statuses/results remain, unfinished athletes receive an append-only pending revision and require source review. closedWithPending/closureNote and an audit record retain the decision. The phone stops and saves its recording when that group becomes closed. Strict complete closure remains available. No LAN group-administration endpoint is added.
