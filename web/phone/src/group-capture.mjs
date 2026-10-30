// This controls only the early-stop warning. It never confirms a result or a bib.
export function allCaptured(view){
 if(!view.heat.start)return false;
 const terminal=r=>['confirmed','dns','dnf','dsq','cancelled'].includes(r.state);
 if(view.runs.every(terminal))return true;
 const expected=view.runs.filter(r=>!['dns','dnf','dsq','cancelled'].includes(r.state)).length;
 if(view.heat.finishes.filter(f=>!f.ignored).length>=expected)return true;
 return view.heat.finishAuthority==='camera-primary'&&(view.heat.visionEvents??[]).filter(v=>!v.ignored).length>=expected;
}
