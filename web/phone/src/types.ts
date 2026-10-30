export interface ClockMap {sessionId:string;offsetUs:number;rttUs:number;jitterUs:number;samples:number;sampledClientUs:number}
export interface Athlete {id:string;bib:string;name:string;team:string}
export interface Impulse {eventId:string;deviceTimeUs:number;hostUs:number;manual:boolean;source:string;raw:string}
export interface Run {id:string;athlete:Athlete;state:string;revision:number;elapsedUs?:number|null;backupElapsedUs?:number;visionEventId?:string;officialSource?:string;visionStatus?:string;reviews:{revision:number}[]}
export interface Heat {id:string;name:string;state:string;version:number;runIds:string[];recordingId:string;start?:Impulse;finishes:{id:string;runId?:string|null;ignored:boolean}[];profile:{unitUs:number;rounding:'cut'|'up'|'round'};evidenceIncomplete:boolean;recordingStopped:boolean;requireRecordingBeforeStart:boolean;acceptingFinishes:boolean;recordingNote:string}
export interface VisionSettings {lineX:number;top:number;bottom:number;left:number;right:number;direction:number;fixedSide:boolean}
export interface RetryState {attempts?:number;nextAttemptAt?:number;blocked?:'authentication'|'conflict';error?:string}
export interface VisionItem extends RetryState {id:string;clipId:string;payload:any;files:Record<string,Blob>;created:number;complete:boolean;submitted?:boolean;evidenceUploaded?:boolean;correspondence?:any;correspondenceReason?:string;verificationState?:'queued'|'checking'|'verified'|'needs-review'|'unmapped';verificationReportPending?:boolean;verificationRetry?:RetryState}
export interface HeatView {heat:Heat & {finishAuthority?:string;visionEvents?:{id:string;runId?:string;estimatedElapsedUs?:number;eligible:boolean;reasons:string[]}[]};runs:Run[]}
export interface Snapshot {protocol:number;appVersion?:string;sessionId:string;timyOnline:boolean;currentHeat:HeatView|null;hostUs:number;simulation:boolean;recordingStopRequests?:{heatId:string;recordingId:string}[]}
export interface Clip extends RetryState {
 id:string;heatId?:string;view?:HeatView;run?:{athlete:Athlete};created:number;recorderStartedClientUs:number;recordingEndClientUs?:number;
 clock?:ClockMap;mime:string;width:number;height:number;fps:number;chunkCount:number;complete:boolean;
 pageClockId:string;previewFrames:{mediaUs:number;presentationUs:number}[];status:string;earlyStop?:boolean;statusPending?:boolean;serverAcknowledged?:boolean;registrationPending?:boolean;model?:string;vision?:VisionSettings;captureFingerprint?:string;frameGeometry?:{width:number;height:number;hashVersion:string};
}
export interface Submission {requestId:string;heatId:string;runId:string;recordingId:string;deviceId:string;expectedReviewRevision:number;framePtsUs:number;finishLine:number;evidenceId:string;note:string;visionEventId?:string}
export interface Outbox extends RetryState {id:string;clipId:string;payload:Submission;image:Blob;metadata:Blob;complete:boolean;reviewUploaded?:boolean;acceptedRevision?:number;created:number}
export interface AnalysisJob {id:string;clipId:string;configHash:string;modelVersion:string;settings:VisionSettings;fromUs:number;toUs:number;cursorUs:number;processed:number;total:number;state:'running'|'paused'|'complete'|'error';pauseReason?:'user'|'live';error?:string;created:number}
export const nowUs=()=>Math.round(performance.now()*1000);
export const id=()=>crypto.randomUUID?.().replaceAll('-','')??Array.from(crypto.getRandomValues(new Uint8Array(16)),v=>v.toString(16).padStart(2,'0')).join('');
export const terminal=(run:Run)=>['confirmed','dns','dnf','dsq','cancelled'].includes(run.state);
export {allCaptured} from './group-capture.mjs';
export function formatTime(run:Run,heat:Heat){if(run.elapsedUs==null)return '待匹配';const q=heat.profile.unitUs,remainder=run.elapsedUs%q;const rounded=(Math.floor(run.elapsedUs/q)+(heat.profile.rounding==='up'&&remainder>0||heat.profile.rounding==='round'&&remainder*2>=q?1:0))*q;return (rounded/1e6).toFixed(Math.round(Math.log10(1e6/q)));}
