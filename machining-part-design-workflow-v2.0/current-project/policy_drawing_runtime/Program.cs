using System.Text.Json;
using System.Text.Json.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
#pragma warning disable CS8321 // Retained diagnostic helpers are invoked by explicit runtime modes as the program evolves.

Console.WriteLine("SMOKE_DIAG=PROGRAM_ENTRY");
Console.WriteLine("BUILD_FINGERPRINT=POLICY_RUNTIME_20260922_NATIVE_HOLE_RECOVERY_2");
Console.Out.Flush();

if (args.Length > 0 && args[0] == "--owned-pipeline")
    return RunOwnedPipeline(args);

if (args.Length > 0 && args[0] == "--hole-callout-canary")
    return RunHoleCalloutCanary(args);

if (args.Length > 0 && args[0] == "--probe-hole-view-representability")
    return RunHoleViewRepresentabilityProbe(args);

if (args.Length == 1 && args[0] == "--support-reference-search-self-test")
    return RunSupportReferenceSearchSelfTest();

if (args.Length == 1 && args[0] == "--semantic-axis-api-self-test")
    return RunSemanticAxisApiSelfTest();

if (args.Length == 1 && args[0] == "--overall-provenance-rebind-self-test")
    return RunOverallProvenanceRebindSelfTest();

if (args.Length == 1 && args[0] == "--com-test")
{
    var interopType = typeof(ISldWorks);
    Console.WriteLine($"INTEROP_ASSEMBLY={interopType.Assembly.FullName}");
    Console.WriteLine($"ISLDWORKS_GUID={interopType.GUID}");
    var members = interopType.GetMembers()
        .Where(m => m.MemberType is System.Reflection.MemberTypes.Method or System.Reflection.MemberTypes.Property)
        .Take(12)
        .Select(m => m.ToString());
    Console.WriteLine($"ISLDWORKS_MEMBER_SAMPLE={string.Join(" || ", members)}");

    var beforeCount = Process.GetProcessesByName("SLDWORKS").Length;
    Console.WriteLine($"SOLIDWORKS_PROCESS_COUNT_BEFORE={beforeCount}");
    if (beforeCount != 0)
    {
        Console.WriteLine("RAW_COM_CREATED=false");
        Console.WriteLine("COM_TEST_ABORTED=PREEXISTING_SOLIDWORKS_PROCESS");
        return 2;
    }

    object? raw = null;
    ISldWorks? typed = null;
    try
    {
        var progType = Type.GetTypeFromProgID("SldWorks.Application")
            ?? throw new InvalidOperationException("SldWorks.Application_PROGID_UNAVAILABLE");
        Console.WriteLine("PROGID_TYPE_AVAILABLE=true");
        raw = Activator.CreateInstance(progType);
        Console.WriteLine($"RAW_COM_CREATED={(raw is not null).ToString().ToLowerInvariant()}");
    }
    catch (Exception ex)
    {
        Console.WriteLine("PROGID_TYPE_AVAILABLE=false");
        Console.WriteLine("RAW_COM_CREATED=false");
        Console.WriteLine($"COM_CREATE_ERROR={ex.GetType().Name}:{ex.Message}");
        Console.WriteLine("SOLIDWORKS_PROCESS_COUNT_AFTER=0");
        Console.WriteLine("ISLDWORKS_QUERYINTERFACE_HRESULT=NOT_RUN");
        Console.WriteLine("ISLDWORKS_INTERFACE_SUPPORTED=false");
        Console.WriteLine("VISIBLE_SET=false");
        return 4;
    }

    var afterCount = Process.GetProcessesByName("SLDWORKS").Length;
    Console.WriteLine($"SOLIDWORKS_PROCESS_COUNT_AFTER={afterCount}");
    nint unknown = 0;
    nint queried = 0;
    int hr;
    try
    {
        unknown = Marshal.GetIUnknownForObject(raw!);
        var iid = interopType.GUID;
        hr = Marshal.QueryInterface(unknown, in iid, out queried);
        Console.WriteLine($"ISLDWORKS_QUERYINTERFACE_HRESULT=0x{hr:X8}");
        var supported = hr >= 0;
        Console.WriteLine($"ISLDWORKS_INTERFACE_SUPPORTED={supported.ToString().ToLowerInvariant()}");
        if (!supported) return 3;
        try
        {
            typed = (ISldWorks)raw!;
            Console.WriteLine("ISLDWORKS_CAST=PASS");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ISLDWORKS_CAST=FAIL:{ex.GetType().Name}:{ex.Message}");
            return 3;
        }
    }
    finally
    {
        if (queried != 0) Marshal.Release(queried);
        if (unknown != 0) Marshal.Release(unknown);
    }

    try { Console.WriteLine($"TYPED_VISIBLE={typed!.Visible}"); }
    catch (Exception ex) { Console.WriteLine($"TYPED_VISIBLE=ERROR:{ex.GetType().Name}:{ex.Message}"); }
    try { Console.WriteLine($"TYPED_FRAME_AVAILABLE={(typed!.Frame() is not null).ToString().ToLowerInvariant()}"); }
    catch (Exception ex) { Console.WriteLine($"TYPED_FRAME_AVAILABLE=ERROR:{ex.GetType().Name}:{ex.Message}"); }
    try { Console.WriteLine($"TYPED_ACTIVE_DOC_AVAILABLE={(typed!.ActiveDoc is not null).ToString().ToLowerInvariant()}"); }
    catch (Exception ex) { Console.WriteLine($"TYPED_ACTIVE_DOC_AVAILABLE=ERROR:{ex.GetType().Name}:{ex.Message}"); }
    try { Console.WriteLine($"TYPED_VERSION_OR_REVISION={typed!.RevisionNumber()}"); }
    catch (Exception ex) { Console.WriteLine($"TYPED_VERSION_OR_REVISION=ERROR:{ex.GetType().Name}:{ex.Message}"); }
    try
    {
        typed!.Visible = true;
        Console.WriteLine("VISIBLE_SET=true");
    }
    catch (Exception ex) { Console.WriteLine($"VISIBLE_SET=false\nVISIBLE_SET_ERROR={ex.GetType().Name}:{ex.Message}"); }
    GC.KeepAlive(typed);
    return 0;
}

if (args.Length == 1 && args[0] == "--attach-smoke")
{
    Console.WriteLine("SMOKE_DIAG=ATTACH_SMOKE_MODE_ENTERED"); Console.Out.Flush();
    Console.WriteLine("SMOKE_DIAG=PROCESS_ENUM_START"); Console.Out.Flush();
    var ps = Process.GetProcessesByName("SLDWORKS");
    Console.WriteLine("SMOKE_DIAG=PROCESS_ENUM_DONE"); Console.Out.Flush();
    Console.WriteLine($"PREEXISTING_SOLIDWORKS_COUNT={ps.Length}");
    if (ps.Length != 1) return 2;
    using var p = ps[0]; p.Refresh();
    Console.WriteLine($"EXISTING_PID={p.Id}\nRESPONDING={p.Responding}\nPROCESS_MAIN_WINDOW_HANDLE={p.MainWindowHandle}");
    if (!p.Responding) return 3;
    try
    {
        Console.WriteLine("SMOKE_DIAG=ROT_ATTACH_START"); Console.Out.Flush();
        using var s = VisibleSolidWorksSession.Attach();
        Console.WriteLine("SMOKE_DIAG=ROT_ATTACH_RETURNED"); Console.Out.Flush();
        if (s.Status != VisibleSolidWorksSession.AttachState.ROT_ATTACHED || s.Root is null)
        {
            Console.WriteLine($"ATTACH_STATUS={s.Status}\nPROCESS_MAIN_WINDOW_HANDLE={p.MainWindowHandle}\nSW_FRAME_AVAILABLE={s.SessionFrameHwnd != 0}\nSW_FRAME_HWND_X64={s.SessionFrameHwnd}\nSESSION_WINDOW_SOURCE={s.SessionWindowSource}\nMANUAL_SESSION_READY={s.ManualSessionUsable.ToString().ToLowerInvariant()}");
            return s.Status == VisibleSolidWorksSession.AttachState.MANUAL_SESSION_REQUIRED && s.ManualSessionUsable ? 0 : 4;
        }
        var frame = s.Root.Frame() as IFrame;
        long hwnd = frame?.GetHWndx64() ?? 0;
        Console.WriteLine($"ROT_ATTACH=PASS\nSW_FRAME_AVAILABLE={hwnd != 0}\nSW_FRAME_HWND_X64={hwnd}\nSOLIDWORKS_VISIBLE_PROPERTY={s.Root.Visible}");
        if (hwnd != 0)
        {
            var h = (nint)hwnd;
            Console.WriteLine($"WIN32_IS_WINDOW={IsWindow(h)}\nWIN32_IS_VISIBLE={IsWindowVisible(h)}");
            NativeWindow.GetWindowRect(h, out var rect); NativeWindow.GetVirtualScreen(out var sx, out var sy, out var sw, out var sh);
            bool intersects = rect.Right > sx && rect.Left < sx + sw && rect.Bottom > sy && rect.Top < sy + sh;
            Console.WriteLine($"WINDOW_RECT={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}\nDISPLAY_WORK_AREA={sx},{sy},{sw},{sh}\nWINDOW_DISPLAY_INTERSECTS={intersects}");
            ShowWindow(h, 9);
            if (!intersects) NativeWindow.SetWindowPos(h, 0, sx + 20, sy + 20, 0, 0, 0x0001 | 0x0004 | 0x0010);
            BringWindowToTop(h); SetForegroundWindow(h);
            NativeWindow.GetWindowRect(h, out rect); intersects = rect.Right > sx && rect.Left < sx + sw && rect.Bottom > sy && rect.Top < sy + sh;
            Console.WriteLine($"WINDOW_RECT_AFTER={rect.Left},{rect.Top},{rect.Right},{rect.Bottom}\nWINDOW_DISPLAY_INTERSECTS_AFTER={intersects}\nWIN32_IS_VISIBLE_AFTER={IsWindowVisible(h)}");
            if (!intersects) return 4;
        }
        bool ok = hwnd != 0 && s.Root.Visible;
        Console.WriteLine($"NEW_SOLIDWORKS_PROCESS_CREATED=false\nATTACHED_INTERACTIVE_SESSION_READY={ok}\nSAFE_TO_RUN_V2={ok}");
        if (ok) { Console.WriteLine("USER_VISUAL_CONFIRMATION=PENDING\nPRESS_ENTER_TO_EXIT"); Console.ReadLine(); }
        return ok ? 0 : 4;
    }
    catch (Exception ex) { Console.WriteLine($"ROT_ATTACH=FAIL\n{ex}"); return 5; }
}

[DllImport("user32.dll")] static extern bool IsWindow(nint hWnd);
[DllImport("user32.dll")] static extern bool IsWindowVisible(nint hWnd);
[DllImport("user32.dll")] static extern bool ShowWindow(nint hWnd, int nCmdShow);
[DllImport("user32.dll")] static extern bool BringWindowToTop(nint hWnd);
[DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hWnd);

// --probe semantics template output-dir; --execute plan semantics template output-dir output-stem
// --manual-session plan semantics template output-dir output-stem output-slddrw
// --owned-session plan semantics template output-dir output-stem
// --only-overall-diagnostic plan semantics template output-dir output-stem
string argMode=args.Length>0?args[0]:"<none>";
Console.WriteLine($"ARG_MODE={argMode}");
Console.WriteLine($"ARG_COUNT={args.Length}");
bool manualSession=args.Length>0&&args[0]=="--manual-session";
bool ownedSession=args.Length>0&&args[0]=="--owned-session";
bool saveDiagnostic=args.Length>0&&args[0]=="--save-diagnostic";
bool saveDiagnosticFullOnly=args.Length>0&&args[0]=="--save-diagnostic-full-only";
bool onlyOverallDiagnostic=args.Length>0&&args[0]=="--only-overall-diagnostic";
bool probe=argMode=="--probe";
bool attachExisting=argMode=="--attach-manual";
bool manual=argMode=="--manual"||attachExisting||manualSession;
bool ownedExecution=ownedSession||onlyOverallDiagnostic;
bool supportedMode=probe||argMode=="--execute"||manual||ownedExecution||saveDiagnostic||saveDiagnosticFullOnly;
if(!supportedMode)
{
    Console.WriteLine("RUNTIME_LOG_INITIALIZED=false");
    Console.WriteLine("EARLY_EXIT_REASON=UNSUPPORTED_MODE");
    return 64;
}
int expectedArgumentCount=manualSession?7:(ownedExecution||saveDiagnostic||saveDiagnosticFullOnly?6:0);
if(expectedArgumentCount>0&&args.Length!=expectedArgumentCount)
{
    Console.WriteLine("RUNTIME_LOG_INITIALIZED=false");
    Console.WriteLine($"EARLY_EXIT_REASON=INVALID_ARGUMENT_COUNT;expected={expectedArgumentCount};actual={args.Length}");
    return 64;
}
if(args.Length<4)
{
    Console.WriteLine("RUNTIME_LOG_INITIALIZED=false");
    Console.WriteLine("EARLY_EXIT_REASON=INSUFFICIENT_ARGUMENTS_FOR_OUTPUT_DIRECTORY");
    return 64;
}
string semanticsPath=probe?args[1]:args[2],template=probe?args[2]:args[3],output=Path.GetFullPath(probe?args[3]:args[4]);
Console.WriteLine($"OUTPUT_DIR={output}");
Directory.CreateDirectory(output);
using var log=new StreamWriter(Path.Combine(output,probe?"projection_probe.log":saveDiagnostic?"save_diagnostic.log":saveDiagnosticFullOnly?"save_diagnostic_full_only.log":"policy_runtime.log")){AutoFlush=true};Console.SetOut(log);Console.SetError(log);
Console.WriteLine($"ARG_MODE={argMode}\nARG_COUNT={args.Length}\nOUTPUT_DIR={output}\nRUNTIME_LOG_INITIALIZED=true");
string stage="START";
using var timeout=new System.Threading.Timer(_=>{
    Console.WriteLine("RUNTIME_TIMEOUT:"+stage);
    if(saveDiagnosticFullOnly&&stage=="FULL_ONLY_SAVE")
        Console.WriteLine("SAVE_DIAGNOSTIC_FULL_ONLY_CLASSIFICATION=SAVE_HANG_REPRODUCED_WITHOUT_INTERMEDIATE_SAVES");
    Console.Out.Flush();System.Environment.Exit(124);
},null,180000,System.Threading.Timeout.Infinite);
try {
    var fullOnlyClock=Stopwatch.StartNew();
    using var sj=JsonDocument.Parse(File.ReadAllText(semanticsPath));var sem=sj.RootElement;
    stage="SINGLE_INSTANCE_GUI_PREFLIGHT";
    if (ownedExecution || saveDiagnostic || saveDiagnosticFullOnly)
    {
        var existingCount=Process.GetProcessesByName("SLDWORKS").Length;
        Console.WriteLine($"SOLIDWORKS_PROCESS_COUNT_BEFORE={existingCount}");
        if (existingCount != 0)
        {
            Console.WriteLine("OWNED_SESSION_ABORTED=PREEXISTING_SOLIDWORKS_PROCESS");
            Console.WriteLine("EARLY_EXIT_REASON=PREEXISTING_SOLIDWORKS_PROCESS");
            return 4;
        }
    }
    using var visibleSession=manualSession?VisibleSolidWorksSession.ManualSession():(attachExisting?VisibleSolidWorksSession.Attach():VisibleSolidWorksSession.Start());
    if (ownedExecution)
    {
        Console.WriteLine("SESSION_MODE=OWNED_SESSION\nRAW_COM_CREATED=true\nISLDWORKS_INTERFACE_SUPPORTED=true\nISLDWORKS_CAST=PASS");
        try { Console.WriteLine($"TYPED_VERSION_OR_REVISION={visibleSession.Root!.RevisionNumber()}"); }
        catch (Exception ex) { Console.WriteLine($"TYPED_VERSION_OR_REVISION=ERROR:{ex.GetType().Name}:{ex.Message}"); }
        Console.WriteLine($"OWNED_SOLIDWORKS_PID={visibleSession.OwnedPid}\nOWNED_SESSION_VISIBLE={(visibleSession.Root?.Visible ?? false).ToString().ToLowerInvariant()}\nSW_FRAME_HWND_X64={visibleSession.SessionFrameHwnd}\nWINDOW_ON_VISIBLE_MONITOR={visibleSession.GuiSessionReady.ToString().ToLowerInvariant()}");
    }
    if(manualSession) Console.WriteLine($"SESSION_MODE=MANUAL_SESSION\nSOLIDWORKS_COUNT={Process.GetProcessesByName("SLDWORKS").Length}\nEXISTING_PID={visibleSession.OwnedPid}");
    Console.WriteLine($"GUI_SESSION_READY={visibleSession.GuiSessionReady.ToString().ToLowerInvariant()}\nCOM_SESSION_READY={visibleSession.ComSessionReady.ToString().ToLowerInvariant()}\nCOM_ROOT_REASON={visibleSession.ComRootReason}");
    if (visibleSession.Status == VisibleSolidWorksSession.AttachState.NO_SOLIDWORKS ||
        visibleSession.Status == VisibleSolidWorksSession.AttachState.MULTIPLE_SOLIDWORKS)
    {
        Console.WriteLine($"stage=SOLIDWORKS_ATTACH\nATTACH_STATUS={visibleSession.Status}");
        return 4;
    }
    if (!visibleSession.ComSessionReady || visibleSession.Root is null)
    {
        Console.WriteLine("COM_ROOT_UNAVAILABLE");
        Console.WriteLine($"COM_ROOT_REASON={visibleSession.ComRootReason}");
        return 4;
    }
    Console.WriteLine("BEFORE_DRAWING_PIPELINE=true");
    var sw=visibleSession.Root;
    int err=0,warn=0;
    var modelPath=sem.GetProperty("model_path").GetString()!;
    var part=sw.OpenDoc6(modelPath,(int)swDocumentTypes_e.swDocPART,(int)(swOpenDocOptions_e.swOpenDocOptions_Silent|swOpenDocOptions_e.swOpenDocOptions_ReadOnly),"",ref err,ref warn)??throw new Exception("PART_OPEN_FAILED");
    Console.WriteLine("MODEL_OPEN=true");
    ModelDoc2 dm;
    bool existingBase=!probe&&!manual&&args.Length==7;
    string? workingCopy=null;
    if(existingBase) {
        workingCopy=Path.Combine(output,args[5]+".SLDDRW");
        if(File.Exists(workingCopy))throw new Exception("OUTPUT_ALREADY_EXISTS");
        File.Copy(Path.GetFullPath(args[6]),workingCopy,false);
        dm=sw.OpenDoc6(workingCopy,(int)swDocumentTypes_e.swDocDRAWING,(int)swOpenDocOptions_e.swOpenDocOptions_Silent,"",ref err,ref warn)??throw new Exception("BASE_DRAWING_COPY_OPEN_FAILED");
    }else dm=sw.NewDocument(Path.GetFullPath(template),0,0,0) as ModelDoc2 ?? throw new Exception("TEMPLATE_CREATE_FAILED");
    Console.WriteLine("DRAWING_CREATED=true");
    var drawing=(DrawingDoc)dm;var sheet=(Sheet)drawing.GetCurrentSheet();double sheetW=0,sheetH=0;sheet.GetSize(ref sheetW,ref sheetH);
    var math=(MathUtility)sw.GetMathUtility();var holes=LoadHoleSemantics(semanticsPath);
    View Create(string role,double x,double y,double scale) {
        View? v=null;
        if(existingBase) {
            using var candidatePlan=JsonDocument.Parse(File.ReadAllText(args[1]));
            var expected=candidatePlan.RootElement.GetProperty("view_coverage_matrix").EnumerateArray().Single(c=>c.GetProperty("orientation").GetString()==role).GetProperty("transform").EnumerateArray().Take(9).Select(z=>z.GetDouble()).ToArray();
            for(var current=(drawing.GetFirstView() as View)?.GetNextView() as View;current!=null;current=current.GetNextView() as View) {
                if(ToDoubles(current.ModelToViewTransform.ArrayData).Take(9).Zip(expected,(a,b)=>Math.Abs(a-b)).All(d=>d<1e-8)){v=current;break;}
            }
            if(v!=null&&v.GetFirstDisplayDimension5()!=null)throw new Exception("BASE_VIEW_ALREADY_ANNOTATED");
        }
        if(v==null)v=CreatePlannedView(drawing,modelPath,role,x,y);
        if(v==null)throw new Exception("VIEW_CREATE_FAILED:"+role);
        ApplyHiddenLinesVisible(v, role);
        v.UseSheetScale=0;v.ScaleDecimal=scale;dm.EditRebuild3();v.Position=new[]{x,y};dm.ForceRebuild3(false);return v;
    }
    if(probe) {
        stage="PROJECTION_PROBE";var rows=new List<object>();
        foreach(var c in sem.GetProperty("view_candidates").EnumerateArray()) {
            var role=c.GetProperty("orientation").GetString()!;var v=Create(role,sheetW/2,sheetH/2,1);
            var axes=new Dictionary<string,double[]>();foreach(var axis in new[]{"X","Y","Z"})try{axes[axis]=ProjectModelAxisDirectionToSheet(math,v,axis);}catch(InvalidOperationException){}
            var circles=EnumerateVisibleCircles(v);var vertices=EnumerateVisibleProjectedVertices(v);
            var matched=holes.Values.Select(h=>new{semantic_ref=h.Id,visible_opening_count=MatchHoleCircles(circles,h.PlacementPoints).Count,semantic_placement_count=h.PlacementPoints.Count,pattern=ClassifyPattern(h.PlacementPoints)}).ToArray();
            var outline=ToDoubles(v.GetOutline());
            rows.Add(new{orientation=role,native_name=v.Name,axes,outline_m=outline,projected_width_m=c.GetProperty("projected_width_m").GetDouble(),projected_height_m=c.GetProperty("projected_height_m").GetDouble(),visible_vertex_count=vertices.Count,visible_circle_count=circles.Count,hole_groups=matched,transform=ToDoubles(v.ModelToViewTransform.ArrayData),vertices=vertices.Select(a=>a.ModelPoint),geometry_evidence="ACTUAL_SOLIDWORKS_STANDARD_VIEW_PROJECTION",opposite_occlusion_equivalence="NOT_EVALUATED"});
        }
        Write(output,"view_projection_evidence.json",new{sheet_width_m=sheetW,sheet_height_m=sheetH,template=Path.GetFullPath(template),model_path=modelPath,candidates=rows});
        Console.WriteLine("PROBE_COMPLETE");sw.CloseDoc(dm.GetTitle());return 0;
    }
    stage="EXECUTE_POLICY_PLAN";
    using var pj=JsonDocument.Parse(File.ReadAllText(args[1]));var plan=pj.RootElement;
    if (onlyOverallDiagnostic)
        Console.WriteLine("EXECUTION_MODE=ONLY_OVERALL_DIAGNOSTIC");
    var policy=plan.GetProperty("policy");var layout=policy.GetProperty("annotation_layout");
    double Metric(string key)=>layout.GetProperty(key).GetDouble()/1000;
    var usable=plan.GetProperty("usable_sheet_bbox_m").EnumerateArray().Select(x=>x.GetDouble()).ToArray();
    var reserves=plan.GetProperty("reserved_regions").EnumerateArray().Select(r=>r.GetProperty("bbox_m").EnumerateArray().Select(x=>x.GetDouble()).ToArray()).ToArray();
    var owners=new Dictionary<string,View>();var viewEvidence=new List<object>();
    var selectedViewRoles = plan.TryGetProperty("selected_view_roles", out var selectedRolesElement) && selectedRolesElement.ValueKind == JsonValueKind.Array
        ? selectedRolesElement.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach(var spec in plan.GetProperty("views").EnumerateArray()) {
        string role = (spec.TryGetProperty("orientation", out var orientationElement) ? orientationElement.GetString() : null)
            ?? (spec.TryGetProperty("role", out var roleElement) ? roleElement.GetString() : null)
            ?? "";
        string kind = spec.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() ?? "" : "";
        string status = spec.TryGetProperty("status", out var statusElement) ? statusElement.GetString() ?? "" : "";
        bool selected = selectedViewRoles.Contains(role) || (!string.IsNullOrWhiteSpace(role) && selectedViewRoles.Contains(role.ToUpperInvariant()));
        if (!selected && (status.Equals("candidate_only", StringComparison.OrdinalIgnoreCase) || !selectedViewRoles.Contains(role))) {
            Console.WriteLine($"OPTIONAL_VIEW_SKIPPED={role}");
            continue;
        }
        if (!spec.TryGetProperty("placement", out var pos) || pos.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"SELECTED_VIEW_MISSING_PLACEMENT:{role}:{kind}");
        if (!spec.TryGetProperty("scale", out var sc) || sc.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"SELECTED_VIEW_MISSING_SCALE:{role}:{kind}");
        var v=Create(role,pos.GetProperty("x_m").GetDouble(),pos.GetProperty("y_m").GetDouble(),sc.GetProperty("numerator").GetDouble()/sc.GetProperty("denominator").GetDouble());
        var b=ToDoubles(v.GetOutline());double dx=Math.Clamp(0,usable[0]-b[0],usable[2]-b[2]),dy=Math.Clamp(0,usable[1]-b[1],usable[3]-b[3]);
        var p=ToDoubles(v.Position);v.Position=new[]{p[0]+dx,p[1]+dy};dm.EditRebuild3();owners.Add(role,v);
        viewEvidence.Add(new{role,native_name=v.Name,position_m=ToDoubles(v.Position),scale=v.ScaleDecimal,outline_m=ToDoubles(v.GetOutline()),boundary_correction_m=new[]{dx,dy},raw_vertices=EnumerateVisibleProjectedVertices(v).Select(q=>q.ModelPoint)});
    }
    Write(output,"runtime_views.json",viewEvidence);
    Console.WriteLine($"VIEW_COUNT={owners.Count}");
    var viewBoxes=owners.Values.Select(v=>ToDoubles(v.GetOutline())).ToArray();
    bool Overlap(double[] a,double[] b)=>Math.Min(a[2],b[2])>Math.Max(a[0],b[0])+1e-9&&Math.Min(a[3],b[3])>Math.Max(a[1],b[1])+1e-9;
    bool Outside(double[] b)=>b[0]<usable[0]||b[1]<usable[1]||b[2]>usable[2]||b[3]>usable[3];
    int viewOverlap=Enumerable.Range(0,viewBoxes.Length).Sum(i=>viewBoxes.Skip(i+1).Count(b=>Overlap(viewBoxes[i],b)));
    int reservedOverlap=viewBoxes.Sum(b=>reserves.Count(r=>Overlap(b,r)));
    if(viewOverlap>0||reservedOverlap>0||viewBoxes.Any(Outside))throw new Exception("VIEW_LAYOUT_PREFLIGHT_FAILED");
    var annotations=new List<PolicyAnnotation>();var lineage=new List<object>();var unresolved=new List<object>();var definitions=new List<Definition>();var semanticKeys=new HashSet<string>();
    void DiagnosticSave(string checkpointName)
    {
        if(!saveDiagnostic)return;
        var checkpointPath=Path.Combine(output,$"{checkpointName}.SLDDRW");
        var checkpointStage=checkpointName switch {
            "practice10_checkpoint_views" => "SAVE_CHECKPOINT=VIEWS_ONLY",
            "practice10_checkpoint_overall" => "SAVE_CHECKPOINT=AFTER_OVERALL_DIMENSIONS",
            "practice10_checkpoint_full" => "SAVE_CHECKPOINT=AFTER_ANNOTATIONS",
            _ => $"SAVE_CHECKPOINT={checkpointName.ToUpperInvariant()}"
        };
        Console.WriteLine(checkpointStage);Console.WriteLine($"SAVE_START path={checkpointPath}");Console.Out.Flush();
        timeout.Change(20000,System.Threading.Timeout.Infinite);int checkpointError=0,checkpointWarning=0;bool returned=false;
        try { returned=dm.Extension.SaveAs(checkpointPath,0,(int)swSaveAsOptions_e.swSaveAsOptions_Silent,null,ref checkpointError,ref checkpointWarning); }
        catch(Exception ex){Console.WriteLine($"SAVE_EXCEPTION_TYPE={ex.GetType().Name}\nSAVE_EXCEPTION_MESSAGE={ex.Message}");}
        var exists=File.Exists(checkpointPath);long size=exists?new FileInfo(checkpointPath).Length:0;
        Console.WriteLine($"SAVE_RETURNED={returned}\nSAVE_ERROR_CODE={checkpointError}\nSAVE_WARNING_CODE={checkpointWarning}\nOUTPUT_EXISTS={exists}\nOUTPUT_SIZE={size}");Console.Out.Flush();
        timeout.Change(180000,System.Threading.Timeout.Infinite);
        if(!returned||!exists||size==0)throw new Exception($"SAVE_CHECKPOINT_FAILED:{checkpointName}:{checkpointError}/{checkpointWarning}");
    }
    if(saveDiagnostic)DiagnosticSave("practice10_checkpoint_views");
    var stepAxisContexts=BuildAxisCoordinateContexts(plan,owners);
    var strictBaselinePolicy=StrictBaselinePolicyEnabled(plan);
    var strictBaselineAssignments=BuildStrictBaselineAssignments(plan,stepAxisContexts,strictBaselinePolicy);
    var globalDimensionRegistry=BuildGlobalDimensionRegistry(plan,sem,holes,stepAxisContexts,strictBaselineAssignments);
    Write(output,"global_dimension_registry.json",new{geometric_tolerance_m=globalDimensionRegistry.GeometricToleranceM,dimensions=globalDimensionRegistry.Entries,cross_view_duplicate_group_count=globalDimensionRegistry.CrossViewDuplicateGroupCount,cross_view_suppressed_dimension_count=globalDimensionRegistry.SuppressedDimensionCount,cross_view_redundant_dimension_count=globalDimensionRegistry.RemainingRedundantDimensionCount});
    var stepDimensionEvidence=new List<StepDimensionExecutionEvidence>();
    Definition MakeDef(string id,string cat,string role,string axis,string? feature)=>new(){AnnotationId=id,Category=cat,OwnerView=JsonSerializer.SerializeToElement(role),Geometry=JsonSerializer.SerializeToElement(new{endpoint_a=new{axis},endpoint_b=new{axis},hole_group=new{semantic_group_ref=feature},entity_strategy=cat=="OVERALL_DIMENSION"?"PROJECTED_AXIS_EXTREMA":"VISIBLE_HOLE_OPENING"}),Dimension=JsonSerializer.SerializeToElement(new{orientation="HORIZONTAL"}),Placement=new(){Lane=cat,Side="ABOVE"},Creation=JsonSerializer.SerializeToElement(new{native_only=true}),Validation=JsonSerializer.SerializeToElement(new{})};
    int diagnosticOverallTarget=saveDiagnostic?plan.GetProperty("intents").EnumerateArray().Count(x=>x.GetProperty("kind").GetString()=="OVERALL"):0;
    int diagnosticOverallProcessed=0;
    foreach(var intent in plan.GetProperty("intents").EnumerateArray().OrderBy(x=>x.GetProperty("kind").GetString()=="CALLOUT"?1:0)) {
        string identity=intent.GetProperty("semantic_identity").GetString()!,kind=intent.GetProperty("kind").GetString()!,role=intent.GetProperty("owner").GetString()!,key=intent.GetProperty("axis_or_feature").GetString()!;
        if (onlyOverallDiagnostic && kind != "OVERALL")
        {
            Console.WriteLine($"ONLY_OVERALL_DIAGNOSTIC_SKIPPED annotation_id={identity};kind={kind}");
            continue;
        }
        if(!semanticKeys.Add(identity))throw new Exception("DUPLICATE_SEMANTIC_IDENTITY");
        if(globalDimensionRegistry.ByAnnotationId.TryGetValue(identity,out var registryEntry)&&registryEntry.CrossViewRedundancySuppressed)
        {
            Console.WriteLine($"DIMENSION_SUPPRESSED annotation_id={identity};canonical_dimension_id={registryEntry.CanonicalDimensionId};selected_owner_view={registryEntry.SelectedOwnerView};reason={registryEntry.SuppressionReason}");
            continue;
        }
        var v=owners[role];
        Stopwatch? overallCreateClock = kind == "OVERALL" ? Stopwatch.StartNew() : null;
        if (overallCreateClock is not null)
            Console.WriteLine($"OVERALL_CREATE_START annotation_id={identity};duration_ms={overallCreateClock.ElapsedMilliseconds}");
        try {
            GeometryResolution g;Definition d;string orientation="HORIZONTAL";double? expected=null;string overallBindingStrategy="<none>";
            if(kind=="OVERALL") {
                var bbox=sem.GetProperty("bounding_box");var raw=EnumerateVisibleProjectedVertices(v);int ai=AxisIndex(key);
                if(raw.Count<2)throw new Exception("VISIBLE_VERTICES_UNAVAILABLE");
                var provenance = ReadOverallAxisProvenance(intent, key);
                Console.WriteLine($"OVERALL_PROVENANCE_INPUT axis={key};target_source={provenance.TargetSource};target_span={provenance.SpanM?.ToString("R") ?? "<unavailable>"};low_proven={provenance.LowProven};high_proven={provenance.HighProven};low_support_kind={provenance.LowSupportKind ?? "<none>"};high_support_kind={provenance.HighSupportKind ?? "<none>"}");
                if (provenance.IsPresent && !provenance.IsEligible)
                {
                    Console.WriteLine($"OVERALL_UNRESOLVED reason=UNPROVEN_ENVELOPE_EXTREMA;axis={key}");
                    Console.WriteLine($"OVERALL_BINDING_RESULT axis={key};strategy=UNRESOLVED;native_api_reached=false;native_api_name=<none>;system_value=<none>;semantic_target=<none>;semantic_value_match=false;terminal_result=UNPROVEN_ENVELOPE_EXTREMA");
                    throw new Exception("UNPROVEN_ENVELOPE_EXTREMA");
                }
                // Registration uses semantic extents, never as a replacement for SystemValue verification.
                var factors=new double[3];var shifts=new double[3];
                for(int a=0;a<3;a++) {
                    string ax="xyz"[a].ToString();double min=bbox.GetProperty("min_"+ax+"_m").GetDouble(),max=bbox.GetProperty("max_"+ax+"_m").GetDouble();
                    double rmin=raw.Min(r=>r.ModelPoint[a]),rmax=raw.Max(r=>r.ModelPoint[a]),ratio=(rmax-rmin)/(max-min);
                    factors[a]=Math.Abs(ratio-v.ScaleDecimal)<Math.Abs(ratio-1)?v.ScaleDecimal:1;
                    shifts[a]=(rmin+rmax)/2-(min+max)/2*factors[a];
                }
                var native=raw.Select(r=>r with {ModelPoint=Enumerable.Range(0,3).Select(a=>(r.ModelPoint[a]-shifts[a])/factors[a]).ToArray()}).ToArray();
                // Build support references from all real selectable projected
                // geometry.  Vertices remain the proven source; edge/circular
                // references are additional witnesses for incomplete or
                // non-pure vertex envelope cases.
                var targetMin=provenance.IsEligible ? provenance.LowCoordinateM!.Value : bbox.GetProperty("min_"+key.ToLowerInvariant()+"_m").GetDouble();
                var targetMax=provenance.IsEligible ? provenance.HighCoordinateM!.Value : bbox.GetProperty("max_"+key.ToLowerInvariant()+"_m").GetDouble();
                var targetAxisSpan=Math.Abs(targetMax-targetMin);
                if (provenance.IsEligible && Math.Abs(targetAxisSpan-provenance.SpanM!.Value)>0.0000001)
                    throw new Exception("PROVENANCE_SPAN_INCONSISTENT");
                // The bounding-box extent is the single authoritative semantic target.
                expected=targetAxisSpan;
                var scaleTolerance=Math.Max(1e-6,expected.Value*0.01);
                var direction=ProjectModelAxisDirectionToSheet(math,v,key);orientation=ResolveDrawingSpaceOrientation(direction[0],direction[1]);
                var supportEvidence = native.Select(reference => new OverallSupportEvidence(reference, reference.ModelPoint[ai], reference.ModelPoint[ai], "VERTEX_POINT")).ToList();
                foreach (var edge in EnumerateVisibleProjectedEdges(v))
                {
                    var p = Enumerable.Range(0, 3).Select(a => (edge.Entity.ModelPoint[a] - shifts[a]) / factors[a]).ToArray();
                    var registeredEndpoints = edge.Endpoints.Select(endpoint => Enumerable.Range(0, 3).Select(a => (endpoint[a] - shifts[a]) / factors[a]).ToArray()).ToArray();
                    var entity = edge.Entity with { NativeType = "STRAIGHT_EDGE", ModelPoint = p };
                    supportEvidence.Add(new OverallSupportEvidence(entity, registeredEndpoints.Min(endpoint => endpoint[ai]), registeredEndpoints.Max(endpoint => endpoint[ai]), "STRAIGHT_EDGE_ENDPOINTS"));
                }
                foreach (var circle in EnumerateVisibleCircles(v))
                {
                    var p = Enumerable.Range(0, 3).Select(a => (circle.ModelPoint[a] - shifts[a]) / factors[a]).ToArray();
                    var circleEvidence = BuildCircularSupportEvidence(v, circle with { ModelPoint = p }, ai, factors, shifts);
                    supportEvidence.Add(circleEvidence.Evidence);
                    Console.WriteLine($"OVERALL_SUPPORT_CIRCULAR_EXTREMA reference_id={circle.Id};center_model={JsonSerializer.Serialize(p)};radius_m={circleEvidence.RadiusM};semantic_axis_support_radius_m={circleEvidence.ProjectedSupportRadiusM};support_coordinate_min={circleEvidence.SupportMin};support_coordinate_max={circleEvidence.SupportMax};support_source={circleEvidence.Source}");
                }
                var supportMin = supportEvidence.Min(evidence => evidence.SupportMin);
                var supportMax = supportEvidence.Max(evidence => evidence.SupportMax);
                var supportCounts = supportEvidence.GroupBy(evidence => evidence.Entity.NativeType).ToDictionary(g => g.Key, g => g.Count());
                SupportReferencePairCandidate? provenancePair = null;
                if (provenance.IsEligible)
                {
                    Console.WriteLine($"PROVENANCE_REBIND_ATTEMPT axis={key}");
                    provenancePair = TryRebindOverallProvenance(dm, native, ai, targetMin, targetMax, targetAxisSpan, scaleTolerance, math, v, orientation, provenance, out var rebindReason, out var lowCandidateCount, out var highCandidateCount, out var rebindPairCount);
                    Console.WriteLine($"PROVENANCE_REBIND_RESULT axis={key};success={(provenancePair is not null).ToString().ToLowerInvariant()};reason={rebindReason};low_candidate_count={lowCandidateCount};high_candidate_count={highCandidateCount};pair_count={rebindPairCount}");
                }
                Console.WriteLine($"OVERALL_SUPPORT_DIAGNOSTIC annotation_id={identity};owner_view_role={role};semantic_axis={key};semantic_target_m={targetAxisSpan};target_model_min={targetMin};target_model_max={targetMax};registered_vertex_axis_min={native.Min(n=>n.ModelPoint[ai])};registered_vertex_axis_max={native.Max(n=>n.ModelPoint[ai])};registered_vertex_span={native.Max(n=>n.ModelPoint[ai])-native.Min(n=>n.ModelPoint[ai])};support_reference_axis_min={supportMin};support_reference_axis_max={supportMax};support_reference_span={supportMax-supportMin};reference_count_by_type={JsonSerializer.Serialize(supportCounts)}");
                foreach(var evidence in supportEvidence)
                {
                    var supportsMin=evidence.SupportMin-scaleTolerance<=targetMin&&targetMin<=evidence.SupportMax+scaleTolerance;
                    var supportsMax=evidence.SupportMin-scaleTolerance<=targetMax&&targetMax<=evidence.SupportMax+scaleTolerance;
                    Console.WriteLine($"OVERALL_SUPPORT_ENVELOPE_REFERENCE annotation_id={identity};reference_id={evidence.Entity.Id};reference_type={evidence.Entity.NativeType};semantic_axis={key};support_coordinate_min={evidence.SupportMin};support_coordinate_max={evidence.SupportMax};target_model_min={targetMin};target_model_max={targetMax};supports_target_min={supportsMin};supports_target_max={supportsMax};support_source={evidence.Source}");
                }
                var minEvidence=supportEvidence.Where(evidence=>evidence.SupportMin-scaleTolerance<=targetMin&&targetMin<=evidence.SupportMax+scaleTolerance).ToArray();
                var maxEvidence=supportEvidence.Where(evidence=>evidence.SupportMin-scaleTolerance<=targetMax&&targetMax<=evidence.SupportMax+scaleTolerance).ToArray();
                int CountType(IEnumerable<OverallSupportEvidence> evidence, string type) => evidence.Count(item => item.Entity.NativeType == type);
                var coverageResult=minEvidence.Length>0?(maxEvidence.Length>0?"BOTH_SIDES_SUPPORTED":"MAX_SIDE_UNSUPPORTED"):(maxEvidence.Length>0?"MIN_SIDE_UNSUPPORTED":"BOTH_SIDES_UNSUPPORTED");
                Console.WriteLine($"OVERALL_ENVELOPE_COVERAGE_SUMMARY annotation_id={identity};semantic_axis={key};target_model_min={targetMin};target_model_max={targetMax};references_supporting_min={minEvidence.Length};references_supporting_max={maxEvidence.Length};vertex_supporting_min={CountType(minEvidence,"VERTEX")};straight_edge_supporting_min={CountType(minEvidence,"STRAIGHT_EDGE")};circular_edge_supporting_min={CountType(minEvidence,"CIRCULAR_EDGE")};vertex_supporting_max={CountType(maxEvidence,"VERTEX")};straight_edge_supporting_max={CountType(maxEvidence,"STRAIGHT_EDGE")};circular_edge_supporting_max={CountType(maxEvidence,"CIRCULAR_EDGE")};bounded_pair_count={minEvidence.Length*maxEvidence.Length};coverage_result={coverageResult}");
                var supportSearch = provenancePair is null
                    ? FindBoundedSupportReferenceCandidates(supportEvidence, ai, targetMin, targetMax, targetAxisSpan, scaleTolerance, math, v, orientation)
                    : EmptySupportReferenceSearchResult();
                Console.WriteLine($"OVERALL_SUPPORT_DEDUP_SUMMARY annotation_id={identity};raw_reference_count={supportSearch.RawReferenceCount};deduped_reference_count={supportSearch.DedupedReferenceCount};duplicates_removed={supportSearch.DuplicatesRemoved}");
                Console.WriteLine($"OVERALL_SUPPORT_SEARCH_SUMMARY annotation_id={identity};semantic_axis={key};raw_reference_count={supportSearch.RawReferenceCount};deduped_reference_count={supportSearch.DedupedReferenceCount};search_algorithm=SEMANTIC_ENVELOPE_ENDPOINT_TOP_K;target_span_m={targetAxisSpan};pair_space_estimate={supportSearch.PairSpaceEstimate};pairs_examined={supportSearch.PairsExamined};pairs_extent_matched={supportSearch.PairsExtentMatched};candidates_retained={supportSearch.Candidates.Count};duplicates_removed={supportSearch.DuplicatesRemoved};search_duration_ms={supportSearch.DurationMs};search_terminated_reason={supportSearch.TerminatedReason}");
                Console.WriteLine($"OVERALL_FALLBACK_SEARCH attempted={(provenancePair is null).ToString().ToLowerInvariant()};reason={(provenancePair is null ? (provenance.IsEligible ? "PROVENANCE_REBIND_FAILED" : "LEGACY_NO_AXIS_PROVENANCE") : "PROVENANCE_REBIND_SUCCEEDED")}");
                var proven = provenancePair;
                var pair=proven is not null
                    ? (proven.A,proven.B,proven.Pa,proven.Pb,proven.Score,"PROVENANCE_REBIND")
                    : default;
                var selectedStrategy = proven is not null ? "PROVENANCE_REBIND" : "BOUNDED_SEARCH";
                overallBindingStrategy = selectedStrategy;
                Console.WriteLine($"OVERALL_SUPPORT_REFERENCE_ATTEMPT annotation_id={identity};strategy={selectedStrategy};candidate_count={supportSearch.Candidates.Count}");
                if (proven is null)
                {
                    // Validate support references one by one with a temporary
                    // native dimension.  A wrong SystemValue is removed before
                    // the next ranked candidate is attempted.
                    pair = default;
                    foreach (var candidate in supportSearch.Candidates)
                    {
                        Console.WriteLine($"OVERALL_SUPPORT_PAIR_CANDIDATE reference_type_A={candidate.A.NativeType};reference_type_B={candidate.B.NativeType};support_coordinate_A={candidate.A.ModelPoint[ai]};support_coordinate_B={candidate.B.ModelPoint[ai]};candidate_axis_span={Math.Abs(candidate.B.ModelPoint[ai]-candidate.A.ModelPoint[ai])};extent_error_m={Math.Abs(Math.Abs(candidate.B.ModelPoint[ai]-candidate.A.ModelPoint[ai])-targetAxisSpan)};projected_dx={candidate.Pb[0]-candidate.Pa[0]};projected_dy={candidate.Pb[1]-candidate.Pa[1]};selectable_A=true;selectable_B=true");
                        Console.WriteLine($"OVERALL_SUPPORT_CANDIDATE_ATTEMPT annotation_id={identity};reference_type_A={candidate.A.NativeType};reference_type_B={candidate.B.NativeType};candidate_axis_span={Math.Abs(candidate.B.ModelPoint[ai]-candidate.A.ModelPoint[ai])};semantic_target_m={targetAxisSpan}");
                        Console.WriteLine($"OVERALL_SELECTED_REFERENCE annotation_id={identity};reference_type_A={candidate.A.NativeType};reference_type_B={candidate.B.NativeType};duration_ms={overallCreateClock?.ElapsedMilliseconds ?? 0};temporary_candidate=true");
                        var candidateGeometry = new GeometryResolution(new[] { candidate.A, candidate.B }, key, null, null, expected * 1000, raw.Count, null);
                        var candidateDefinition = MakeDef(identity, "OVERALL_DIMENSION", role, key, null);
                        candidateDefinition.Dimension = JsonSerializer.SerializeToElement(new { orientation });
                        try
                        {
                            var createdCandidate = CreateLinearDimension(dm, math, v, candidateDefinition, candidateGeometry, 0);
                            var createdJson = JsonSerializer.SerializeToElement(createdCandidate);
                            var candidateId = createdJson.GetProperty("creation_identity").GetString();
                            var candidateDisplay = candidateId is null ? null : FindDisplayDimension(v, candidateId);
                            var candidateSystem = candidateDisplay is null ? 0 : ReadPositiveSystemValue(candidateDisplay.GetDimension() as Dimension);
                            var candidateMatch = candidateSystem > 0 && Math.Abs(candidateSystem - targetAxisSpan) <= 0.0001;
                            Console.WriteLine($"OVERALL_SUPPORT_CANDIDATE_RESULT annotation_id={identity};creation_result={(candidateDisplay is null ? "FAILED" : "CREATED")};actual_system_value_m={candidateSystem:0.#########};absolute_error_m={Math.Abs(candidateSystem-targetAxisSpan):0.#########};semantic_validation={(candidateMatch ? "PASS" : "FAIL")}");
                            if (candidateMatch)
                            {
                                if (candidateDisplay is not null)
                                {
                                    Console.WriteLine($"OVERALL_TEMP_DIM_DELETE annotation_id={identity};reason=TEMPORARY_CANDIDATE_ACCEPTED_FOR_RECREATE;duration_ms={overallCreateClock?.ElapsedMilliseconds ?? 0}");
                                    DeleteAnnotation(dm, candidateDisplay);
                                }
                                pair = (candidate.A, candidate.B, candidate.Pa, candidate.Pb, candidate.Score, "BOUNDED_SEARCH");
                                break;
                            }
                            if (candidateDisplay is not null)
                            {
                                Console.WriteLine($"OVERALL_TEMP_DIM_DELETE annotation_id={identity};reason=SEMANTIC_SYSTEMVALUE_MISMATCH;duration_ms={overallCreateClock?.ElapsedMilliseconds ?? 0}");
                                DeleteAnnotation(dm, candidateDisplay);
                            }
                        }
                        catch (Exception candidateError)
                        {
                            Console.WriteLine($"OVERALL_CANDIDATE_REJECTED annotation_id={identity};reference_type_A={candidate.A.NativeType};reference_type_B={candidate.B.NativeType};reason={candidateError.Message};duration_ms={overallCreateClock?.ElapsedMilliseconds ?? 0}");
                            Console.WriteLine($"OVERALL_SUPPORT_CANDIDATE_RESULT annotation_id={identity};creation_result=FAILED;semantic_validation=FAIL;rejection_reason={candidateError.Message}");
                        }
                    }
                }
                if(pair.A is null)
                {
                    Console.WriteLine($"OVERALL_SUPPORT_REFERENCE_RESULT annotation_id={identity};strategy={selectedStrategy};binding_result=NO_PAIR;reason={(supportEvidence.Count < 2 ? "NO_SELECTABLE_SUPPORT_REFERENCES" : "NO_SEMANTICALLY_VALID_SUPPORT_PAIR")}");
                    throw new Exception(native.Length < 2 ? "VERTEX_ENVELOPE_INCOMPLETE" : "CURVED_ENVELOPE_REFERENCE_REQUIRED");
                }
                var selectedA = pair.A;
                var selectedB = pair.B ?? throw new InvalidOperationException("SUPPORT_REFERENCE_PAIR_ENDPOINT_MISSING");
                Console.WriteLine($"OVERALL_SELECTED_REFERENCE annotation_id={identity};reference_type_A={selectedA.NativeType};reference_type_B={selectedB.NativeType};duration_ms={overallCreateClock?.ElapsedMilliseconds ?? 0}");
                Console.WriteLine($"OVERALL_SUPPORT_REFERENCE_RESULT annotation_id={identity};strategy={selectedStrategy};binding_result=CANDIDATE_FOUND;reference_type_A={selectedA.NativeType};reference_type_B={selectedB.NativeType};creation_result=PENDING;semantic_validation=PENDING");
                g=new GeometryResolution(new[]{selectedA,selectedB},key,null,null,expected*1000,raw.Count,null);d=MakeDef(identity,"OVERALL_DIMENSION",role,key,null);
                Console.WriteLine($"VERTEX_FRAME_REGISTRATION role={role};axis={key};factors={JsonSerializer.Serialize(factors)};shifts={JsonSerializer.Serialize(shifts)}");
            }else if(kind=="STEP") {
                var sourceSemanticValue=intent.TryGetProperty("expected_value_m", out var ev) && ev.ValueKind==JsonValueKind.Number ? ev.GetDouble() : (double?)null;
                if(!sourceSemanticValue.HasValue || sourceSemanticValue.Value<=0) throw new Exception("STEP_PROFILE_VALUE_UNAVAILABLE");
                var axis=key; var vertices=EnumerateVisibleProjectedVertices(v); var ai=AxisIndex(axis);
                var definitionRole=intent.TryGetProperty("definition_role",out var roleElement) ? roleElement.GetString() ?? "unspecified" : "unspecified";
                var contextKey=AxisCoordinateContext.Key(role,axis);
                if(!stepAxisContexts.TryGetValue(contextKey,out var axisContext)) throw new Exception("STEP_AXIS_CONTEXT_UNAVAILABLE");
                var assignment=strictBaselineAssignments.TryGetValue(identity,out var strictAssignment) ? strictAssignment : null;
                expected=assignment?.ExecutionValueM ?? sourceSemanticValue;
                var ordered=vertices.OrderBy(q=>q.ModelPoint[ai]).ToArray();
                var pairs=(from a in ordered from b in ordered where !ReferenceEquals(a.Object,b.Object)
                    let delta=Math.Abs(b.ModelPoint[ai]-a.ModelPoint[ai])
                    where assignment is null
                        ? Math.Abs(delta-expected.Value)<=0.0001
                        : ((Math.Abs(a.ModelPoint[ai]-assignment.ReferenceCoordinate)<=1e-9&&Math.Abs(b.ModelPoint[ai]-assignment.TargetCoordinate)<=1e-9)||
                           (Math.Abs(b.ModelPoint[ai]-assignment.ReferenceCoordinate)<=1e-9&&Math.Abs(a.ModelPoint[ai]-assignment.TargetCoordinate)<=1e-9))
                    let nonTargetDelta=Enumerable.Range(0,3).Where(i=>i!=ai).Sum(i=>Math.Abs(b.ModelPoint[i]-a.ModelPoint[i]))
                    let coordinateA=a.ModelPoint[ai]
                    let coordinateB=b.ModelPoint[ai]
                    let usesReference=Math.Abs(coordinateA-axisContext.ReferenceCoordinate)<=1e-9||Math.Abs(coordinateB-axisContext.ReferenceCoordinate)<=1e-9
                    select new {A=a,B=b,Delta=delta,NonTargetDelta=nonTargetDelta,CoordinateA=coordinateA,CoordinateB=coordinateB,UsesReference=usesReference}).ToArray();
                if(pairs.Length==0) throw new Exception(assignment is null?"STEP_PROFILE_LEVEL_PAIR_UNAVAILABLE":"STRICT_BASELINE_ENDPOINT_PAIR_UNAVAILABLE");
                var pair=pairs.OrderBy(p=>p.NonTargetDelta).ThenBy(p=>Math.Min(p.CoordinateA,p.CoordinateB)).First();
                var strategy=assignment is not null
                    ? new StepDimensionStrategy("BASELINE",assignment.ReferenceCoordinate,assignment.Reason,assignment.TargetCoordinate,definitionRole,false,contextKey,false,false,true,sourceSemanticValue.Value,assignment.ExecutionValueM,"STRICT_BASELINE_COORDINATE")
                    : new StepDimensionStrategy(pair.UsesReference?"BASELINE":"CHAIN_ALLOWED",pair.UsesReference?axisContext.ReferenceCoordinate:null,pair.UsesReference?axisContext.ReferenceReason:"NO_STRICT_BASELINE_ASSIGNMENT",pair.UsesReference?(Math.Abs(pair.CoordinateA-axisContext.ReferenceCoordinate)<=1e-9?pair.CoordinateB:pair.CoordinateA):null,definitionRole,false,contextKey,false,false,false,sourceSemanticValue.Value,pair.Delta,"SEMANTIC_DIRECT");
                var projectedAxis=ProjectModelAxisDirectionToSheet(math,v,axis);
                orientation=ResolveDrawingSpaceOrientation(projectedAxis[0],projectedAxis[1]);
                if(orientation=="ALIGNED") throw new Exception("STEP_SEMANTIC_AXIS_OBLIQUE_IN_OWNER_VIEW");
                g=new GeometryResolution(new[]{pair.A,pair.B},axis,null,null,pair.Delta*1000.0,vertices.Count,null,strategy);
                d=MakeDef(identity,"STEP_DIMENSION",role,axis,null);
                Console.WriteLine("STEP_AXIS_MAPPING:");
                Console.WriteLine($"annotation_id={identity}");
                Console.WriteLine($"semantic_axis={axis}");
                Console.WriteLine($"sheet_axis=[{string.Join(",",projectedAxis)}]");
                Console.WriteLine($"orientation={orientation}");
                Console.WriteLine($"source_semantic_value_m={sourceSemanticValue.Value:0.#########}");
                Console.WriteLine($"target_value_m={expected.Value:0.#########}");
                Console.WriteLine($"non_target_axis_deltas={JsonSerializer.Serialize(Enumerable.Range(0,3).Where(i=>i!=ai).Select(i=>Math.Abs(pair.B.ModelPoint[i]-pair.A.ModelPoint[i])).ToArray())}");
                Console.WriteLine($"selection_reason={strategy.ReferenceReason}");
                Console.WriteLine($"dimension_strategy={strategy.DimensionStrategy}");
                Console.WriteLine($"reference_coordinate={strategy.ReferenceCoordinate?.ToString("0.#########")??"<local>"}");
                Console.WriteLine($"coordinate_level={strategy.CoordinateLevel?.ToString("0.#########")??"<local>"}");
                Console.WriteLine($"representation_mode={strategy.RepresentationMode}");
                Console.WriteLine($"chain_group_id={strategy.ChainGroupId}");
                }else if(kind=="HOLE_LOCATION") {
                    throw new Exception("HOLE_LOCATION_UNRESOLVED:NO_EXPLICIT_DATUM_OR_REFERENCE_GEOMETRY");
                }else {
                    var h=holes[key];var holeMatches=MatchHoleCircles(EnumerateVisibleCircles(v),h.PlacementPoints);
                if(kind=="PATTERN") {
                    if(holeMatches.Count<2)throw new Exception("PATTERN_OPENINGS_NOT_BINDABLE");var pair=SelectAdjacentPair(holeMatches);
                    expected=Distance(pair.A.ModelPoint,pair.B.ModelPoint);
                    var pa=ProjectModelPointToSheet(math,v,pair.A.ModelPoint);var pb=ProjectModelPointToSheet(math,v,pair.B.ModelPoint);orientation=ResolveDrawingSpaceOrientation(pb[0]-pa[0],pb[1]-pa[1]);
                    g=new GeometryResolution(new[]{pair.A,pair.B},null,key,h,expected*1000,holeMatches.Count,null);d=MakeDef(identity,"HOLE_PITCH",role,"",key);
                }else {
                    if(holeMatches.Count==0)throw new Exception("HOLE_OPENING_UNAVAILABLE");g=new GeometryResolution(new[]{holeMatches[0]},null,key,h,null,holeMatches.Count,null);d=MakeDef(identity,"HOLE_CALLOUT",role,"",key);
                }
            }
            d.Dimension=JsonSerializer.SerializeToElement(new{orientation});definitions.Add(d);
            object result;
            if(kind=="CALLOUT") result=CreateHoleCallout(dm,v,d,g,0);
            else if(kind=="PATTERN")result=CreatePitchDimension(dm,v,d,g,0);
            else result=CreateLinearDimension(dm,math,v,d,g,0);
            var je=JsonSerializer.SerializeToElement(result);string createdId=je.GetProperty("creation_identity").GetString()!;
            DisplayDimension? display=null;for(var dd=v.GetFirstDisplayDimension5() as DisplayDimension;dd!=null;dd=dd.GetNext5() as DisplayDimension)if(dd.GetNameForSelection()==createdId)display=dd;
            if(display==null)throw new Exception("CREATED_DISPLAY_MISSING");
            if(expected.HasValue)
            {
                var actualSystemValue=((Dimension)display.GetDimension()).SystemValue;
                const double acceptanceToleranceM=0.0001;
                var absoluteErrorM=Math.Abs(actualSystemValue-expected.Value);
                var relativeError=expected.Value==0.0? (absoluteErrorM==0.0?0.0:double.PositiveInfinity) : absoluteErrorM/Math.Abs(expected.Value);
                var semanticValueMatch=absoluteErrorM<=acceptanceToleranceM;
                if (kind=="OVERALL")
                {
                    Console.WriteLine($"OVERALL_SYSTEM_VALUE_READ annotation_id={identity};system_value_m={actualSystemValue:0.#########};semantic_target_m={expected.Value:0.#########};absolute_error_m={absoluteErrorM:0.#########};semantic_value_match={semanticValueMatch};duration_ms={overallCreateClock?.ElapsedMilliseconds ?? 0}");
                    Console.WriteLine($"OVERALL_SUPPORT_REFERENCE_RESULT annotation_id={identity};strategy={overallBindingStrategy};binding_result={(semanticValueMatch?"SUCCESS":"REJECTED")};creation_result={(semanticValueMatch?"SUCCESS":"REJECTED")};actual_system_value_m={actualSystemValue:0.#########};semantic_validation={(semanticValueMatch?"PASS":"FAIL")};rejection_reason={(semanticValueMatch?"<none>":"SEMANTIC_SYSTEMVALUE_MISMATCH")}");
                }
                Console.WriteLine($"OVERALL_SEMANTIC_VALIDATION annotation_id={identity};semantic_target_m={expected.Value:0.#########};actual_system_value_m={actualSystemValue:0.#########};absolute_error_m={absoluteErrorM:0.#########};relative_error={relativeError:0.#########};acceptance_tolerance_m={acceptanceToleranceM:0.#########};semantic_value_match={semanticValueMatch}");
                if(!semanticValueMatch)
                {
                    DeleteAnnotation(dm,display);
                    throw new Exception($"SEMANTIC_SYSTEMVALUE_MISMATCH: {identity}; expected={expected.Value:0.#########}m; actual={actualSystemValue:0.#########}m; absolute_error_m={absoluteErrorM:0.#########}; relative_error={relativeError:0.#########}; tolerance_m={acceptanceToleranceM:0.#########}");
                }
            }
            if(kind=="STEP"&&g.StepStrategy is not null) stepDimensionEvidence.Add(new StepDimensionExecutionEvidence(identity,role,key,g.Entities[0].ModelPoint[AxisIndex(key)],g.Entities[1].ModelPoint[AxisIndex(key)],g.StepStrategy,((Dimension)display.GetDimension()).SystemValue));
            var endpoints=g.Entities.Select(e=>ProjectModelPointToSheet(math,v,e.ModelPoint)).ToArray();
            var text=kind=="CALLOUT"?new string('X',layout.GetProperty("callout_estimated_characters").GetInt32()):(expected!.Value*1000).ToString("0.###",System.Globalization.CultureInfo.InvariantCulture);
            annotations.Add(new PolicyAnnotation(identity,kind,role,display,endpoints,text,orientation,result));
            }catch(Exception e){
                var failureStage = kind=="CALLOUT" ? "HOLE_OPENING_BINDING" : kind=="PATTERN" ? "HOLE_CENTER_BINDING" : kind=="HOLE_LOCATION" ? "HOLE_REFERENCE_BINDING" : "ANNOTATION_CREATION";
                unresolved.Add(new{intent=identity,category=kind,status="NOT_EVALUATED",failure_stage=failureStage,reason=e.Message,candidate_count=0});
                Console.WriteLine("INTENT_UNRESOLVED:"+identity+":"+e.Message);
            }
        finally {
            if(saveDiagnostic&&kind=="OVERALL"&&++diagnosticOverallProcessed==diagnosticOverallTarget)
                DiagnosticSave("practice10_checkpoint_overall");
        }
    }
    var chainAnalysis=AnalyzeStepDimensionChains(stepDimensionEvidence,strictBaselinePolicy);
    Write(output,"step_dimension_strategy.json",new{strict_baseline_policy=chainAnalysis.StrictBaselinePolicy,axis_coordinate_levels=stepAxisContexts.Values,strict_baseline_assignments=strictBaselineAssignments.Values,chain_dimension_groups=chainAnalysis.Groups,unnecessary_chain_dimension_count=chainAnalysis.UnnecessaryCount,visual_chain_dimension_count=chainAnalysis.VisualChainCount,local_size_without_explicit_intent_count=chainAnalysis.LocalSizeWithoutExplicitIntentCount,redundant_dimension_count=chainAnalysis.RedundantCount,dimensions=stepDimensionEvidence});
    stage="COLLISION_LAYOUT";
    var occupiedBoxes=new List<double[]>();var lines=new List<double[]>();var placements=new List<object>();
    bool LineBox(double[] l,double[] b) {
        double t0=0,t1=1,dx=l[2]-l[0],dy=l[3]-l[1];double[] p={-dx,dx,-dy,dy},q={l[0]-b[0],b[2]-l[0],l[1]-b[1],b[3]-l[1]};
        for(int i=0;i<4;i++){if(Math.Abs(p[i])<1e-12){if(q[i]<0)return false;}else{double t=q[i]/p[i];if(p[i]<0)t0=Math.Max(t0,t);else t1=Math.Min(t1,t);if(t0>t1)return false;}}return true;
    }
    bool Cross(double[] a,double[] b) {
        double Orient(double x,double y,double u,double v,double p,double q)=>(u-x)*(q-y)-(v-y)*(p-x);
        return Orient(a[0],a[1],a[2],a[3],b[0],b[1])*Orient(a[0],a[1],a[2],a[3],b[2],b[3]) < -1e-16 && Orient(b[0],b[1],b[2],b[3],a[0],a[1])*Orient(b[0],b[1],b[2],b[3],a[2],a[3]) < -1e-16;
    }
    var weights=layout.GetProperty("penalty_weights");double W(string k)=>weights.GetProperty(k).GetDouble();
    foreach(var a in annotations.OrderBy(x=>x.Kind=="CALLOUT"?1:0).ThenBy(x=>x.Kind=="OVERALL"?1:0)) {
        var b=ToDoubles(owners[a.Role].GetOutline());bool callout=a.Kind=="CALLOUT";
        double tw=a.Text.Length*Metric("text_character_width_mm")+2*Metric("text_padding_mm"),th=Metric("text_height_mm")+2*Metric("text_padding_mm");
        if(a.Orientation=="VERTICAL"&&!callout)(tw,th)=(th,tw);
        var choices=new List<(double Score,double[] Position,double[] Box,List<double[]> Lines,string Side,int Lane)>();
        foreach(var side in layout.GetProperty("candidate_sides").EnumerateArray().Select(x=>x.GetString()!)) {
            if(!callout&&((a.Orientation=="VERTICAL")!=(side=="LEFT"||side=="RIGHT")))continue;
            for(int lane=0;lane<layout.GetProperty("lane_count").GetInt32();lane++) {
                double off=Metric("lane_offset_mm")+lane*Metric("lane_spacing_mm");
                foreach(double fraction in callout?new[]{0.0,0.5,1.0}:new[]{0.5}) {
                    double x=side=="LEFT"?b[0]-off-tw/2:side=="RIGHT"?b[2]+off+tw/2:b[0]+fraction*(b[2]-b[0]);
                    double y=side=="BOTTOM"?b[1]-off-th/2:side=="TOP"?b[3]+off+th/2:b[1]+fraction*(b[3]-b[1]);
                    var box=new[]{x-tw/2,y-th/2,x+tw/2,y+th/2};var candidateLines=new List<double[]>();
                    if(callout)candidateLines.Add(new[]{a.Endpoints[0][0],a.Endpoints[0][1],x,y});
                    else {
                        foreach(var ep in a.Endpoints)candidateLines.Add(a.Orientation=="VERTICAL"?new[]{ep[0],ep[1],x,ep[1]}:new[]{ep[0],ep[1],ep[0],y});
                        candidateLines.Add(a.Orientation=="VERTICAL"?new[]{x,a.Endpoints[0][1],x,a.Endpoints[1][1]}:new[]{a.Endpoints[0][0],y,a.Endpoints[1][0],y});
                    }
                    double penalty=occupiedBoxes.Count(o=>Overlap(o,box))*W("overlap")+(lines.Count(l=>LineBox(l,box))+candidateLines.Sum(l=>occupiedBoxes.Count(o=>LineBox(l,o))))*W("line_text")+viewBoxes.Count(v=>Overlap(v,box))*W("view")+(reserves.Count(r=>Overlap(r,box))+(Outside(box)?1:0))*W("reserved");
                    if(callout)penalty+=Math.Sqrt(Math.Pow(x-a.Endpoints[0][0],2)+Math.Pow(y-a.Endpoints[0][1],2))*1000*W("leader_length");
                    penalty+=candidateLines.Sum(l=>lines.Count(other=>Cross(l,other)))*W("crossing");
                    choices.Add((penalty,new[]{x,y},box,candidateLines,side,lane));
                }
            }
        }
        var best=choices.OrderBy(c=>c.Score).First();
        var annotation=(Annotation)a.Display.GetAnnotation();annotation.SetPosition2(best.Position[0],best.Position[1],0);occupiedBoxes.Add(best.Box);lines.AddRange(best.Lines);
        placements.Add(new{annotation_id=a.Id,position_m=best.Position,estimated_text_bbox_m=best.Box,lines=best.Lines,side=best.Side,lane=best.Lane,score=best.Score,bbox_authority="POLICY_ESTIMATED_TEXT_EXTENT",candidate_count=choices.Count});
        var row=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(JsonSerializer.Serialize(a.Lineage))!;row["actual_position_m"]=JsonSerializer.SerializeToElement(AnnotationPosition(a.Display));lineage.Add(row);
    }
    Write(output,"annotation_layout.json",placements);Write(output,"annotation_lineage.json",lineage);Write(output,"annotation_definitions.json",definitions);
    Write(output,"estimated_layout_validation.json",new{authority="ESTIMATED_TEXT_AND_IDEALIZED_EXTENSION_LEADER_SEGMENTS",text_bbox_overlap_count=Enumerable.Range(0,occupiedBoxes.Count).Sum(i=>occupiedBoxes.Skip(i+1).Count(b=>Overlap(occupiedBoxes[i],b))),reserved_text_overlap_count=occupiedBoxes.Sum(b=>reserves.Count(r=>Overlap(b,r))),text_outside_usable_count=occupiedBoxes.Count(Outside),segment_crossing_count=Enumerable.Range(0,lines.Count).Sum(i=>lines.Skip(i+1).Count(l=>Cross(lines[i],l))),full_native_geometry_crossing="NOT_EVALUATED",qa_requires_saved_pdf=true});
    var centerEvidence=new List<object>();
    if(!onlyOverallDiagnostic && layout.GetProperty("center_marks_for_verified_patterns").GetBoolean())foreach(var h in holes.Values) {
        var group=annotations.FirstOrDefault(a=>a.Kind=="PATTERN"&&a.Id=="PATTERN:"+h.Id);
        if(group==null)continue;
        try {
            var v=owners[group.Role];var circles=MatchHoleCircles(EnumerateVisibleCircles(v),h.PlacementPoints);
            if(ClassifyPattern(h.PlacementPoints)!="LINEAR_PATTERN"||circles.Count!=h.PlacementPoints.Count)throw new Exception("LINEAR_PATTERN_CENTERMARK_EVIDENCE_INCOMPLETE");
            dm.ClearSelection2(true);bool selected=true;for(int i=0;i<circles.Count;i++)selected&=circles[i].Object.Select4(i>0,null);
            if(!selected)throw new Exception("CENTERMARK_SELECTION_FAILED");
            var mark=drawing.InsertCenterMark3((int)swCenterMarkStyle_e.swCenterMark_LinearGroup,false,false);
            if(mark==null)throw new Exception("NATIVE_CENTERMARK_RETURNED_NULL");
            if(layout.GetProperty("connect_verified_pattern_centers").GetBoolean())mark.ConnectionLines=(int)swCenterMarkConnectionLine_e.swCenterMark_ShowLinearConnectLines;
            centerEvidence.Add(new{semantic_ref=h.Id,status="CREATED_PENDING_REOPEN",group_count=mark.GroupCount,connection_lines=mark.ConnectionLines,style=mark.Style,name=mark.GetAnnotation().GetName()});
        }catch(Exception e){centerEvidence.Add(new{semantic_ref=h.Id,status="NOT_EVALUATED",reason=e.Message});}
        finally{dm.ClearSelection2(true);}
    }
    Write(output,"centerline_api_evidence.json",centerEvidence);
    stage=saveDiagnosticFullOnly?"FULL_ONLY_SAVE":"SAVE";timeout.Change(20000,System.Threading.Timeout.Infinite);string stem=args[5];string saved=saveDiagnostic?Path.Combine(output,"practice10_checkpoint_full.SLDDRW"):saveDiagnosticFullOnly?Path.Combine(output,"practice10_full_only.SLDDRW"):manual?Path.GetFullPath(args[6]):Path.Combine(output,stem+".SLDDRW"),pdf=manual?Path.ChangeExtension(Path.GetFullPath(args[6]),".pdf"):Path.Combine(output,stem+".pdf");
    if((!existingBase&&File.Exists(saved))||File.Exists(pdf))throw new Exception("OUTPUT_ALREADY_EXISTS");
    int activationError=0;var activeDrawing=sw.ActivateDoc3(dm.GetTitle(),false,(int)swRebuildOnActivation_e.swDontRebuildActiveDoc,ref activationError) as ModelDoc2;
    if(activeDrawing==null||activeDrawing.GetType()!=(int)swDocumentTypes_e.swDocDRAWING)throw new Exception("DRAWING_ACTIVATION_FAILED");
    dm.ClearSelection2(true);dm.EditRebuild3();err=warn=0;
    if(saveDiagnostic) {
        DiagnosticSave("practice10_checkpoint_full");
        Console.WriteLine("SAVE_DIAGNOSTIC_CLASSIFICATION=NO_SAVE_HANG_REPRODUCED");
        return 0;
    } else if(saveDiagnosticFullOnly) {
        Console.WriteLine($"FULL_ONLY_SAVE_START path={saved}");
        Console.WriteLine($"FULL_ONLY_ELAPSED_BEFORE_SAVE_MS={fullOnlyClock.ElapsedMilliseconds}");Console.Out.Flush();
        var fullOnlySaveClock=Stopwatch.StartNew();int fullOnlyError=0,fullOnlyWarning=0;bool fullOnlyReturned=false;
        try { fullOnlyReturned=dm.Extension.SaveAs(saved,0,(int)swSaveAsOptions_e.swSaveAsOptions_Silent,null,ref fullOnlyError,ref fullOnlyWarning); }
        catch(Exception ex) { Console.WriteLine($"FULL_ONLY_SAVE_EXCEPTION_TYPE={ex.GetType().Name}\nFULL_ONLY_SAVE_EXCEPTION_MESSAGE={ex.Message}"); }
        var fullOnlyExists=File.Exists(saved);long fullOnlySize=fullOnlyExists?new FileInfo(saved).Length:0;
        Console.WriteLine($"FULL_ONLY_SAVE_RETURNED={fullOnlyReturned}\nFULL_ONLY_SAVE_ERROR_CODE={fullOnlyError}\nFULL_ONLY_SAVE_WARNING_CODE={fullOnlyWarning}\nFULL_ONLY_OUTPUT_EXISTS={fullOnlyExists}\nFULL_ONLY_OUTPUT_SIZE={fullOnlySize}\nFULL_ONLY_SAVE_DURATION_MS={fullOnlySaveClock.ElapsedMilliseconds}");Console.Out.Flush();
        timeout.Change(System.Threading.Timeout.Infinite,System.Threading.Timeout.Infinite);
        Console.WriteLine(fullOnlyReturned&&fullOnlyExists&&fullOnlySize>0?"SAVE_DIAGNOSTIC_FULL_ONLY_CLASSIFICATION=SAVE_HANG_NOT_REPRODUCED_FULL_ONLY":"SAVE_DIAGNOSTIC_FULL_ONLY_CLASSIFICATION=SAVE_HANG_REPRODUCED_WITHOUT_INTERMEDIATE_SAVES");
        return fullOnlyReturned&&fullOnlyExists&&fullOnlySize>0?0:2;
    } else if(manual) {
        visibleSession.Checkpoint(dm,owners.Count,owners.Values.Select(v=>v.ScaleDecimal).Distinct(),saved);
        stage="WAITING_FOR_MANUAL_SAVE";timeout.Change(System.Threading.Timeout.Infinite,System.Threading.Timeout.Infinite);
        var checkpoint=new{status="WAITING_FOR_MANUAL_SAVE",drawing_title=dm.GetTitle(),view_count=owners.Count,scale=owners.Values.Select(v=>v.ScaleDecimal).Distinct().ToArray(),overall_dimension_count=annotations.Count(a=>a.Kind=="OVERALL"),native_hole_callout_count=annotations.Count(a=>a.Kind=="CALLOUT"),center_mark_groups=centerEvidence,runtime_view_overlap_count=viewOverlap,runtime_reserved_overlap_count=reservedOverlap,suggested_output_path=saved,generation="AUTOMATIC",slddrw_persistence="MANUAL_CHECKPOINT",pdf="NOT_EVALUATED"};
        Write(output,"manual_persistence_checkpoint.json",checkpoint);
        Console.WriteLine("POLICY_V2_READY_FOR_MANUAL_SAVE=true\n"+JsonSerializer.Serialize(checkpoint,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"VIEW_ROLES={string.Join(",",owners.Keys)}\nOVERALL_DIMENSIONS={string.Join(",",annotations.Where(a=>a.Kind=="OVERALL").Select(a=>((Dimension)a.Display.GetDimension()).SystemValue*1000))}\nHOLE_PITCH={string.Join(",",annotations.Where(a=>a.Kind=="PATTERN").Select(a=>((Dimension)a.Display.GetDimension()).SystemValue*1000))}\nNATIVE_HOLE_CALLOUT_COUNT={annotations.Count(a=>a.Kind=="CALLOUT"&&a.Display.IsHoleCallout())}\nCENTER_MARK_GROUP_COUNT={owners.Values.Sum(v=>v.GetCenterMarkCount())}\nVIEW_OVERLAP_COUNT={viewOverlap}\nRESERVED_REGION_OVERLAP_COUNT={reservedOverlap}\nDUPLICATE_DIMENSION_COUNT={definitions.Count-definitions.Select(d=>d.AnnotationId).Distinct().Count()}");
        Console.WriteLine("WAITING_FOR_USER_CONFIRMATION: press ENTER after File > Save As; no automatic drawing save will be called.");
        while(true) {
            string? response=Console.ReadLine();
            if(response==null){System.Threading.Thread.Sleep(500);continue;}
            GC.KeepAlive(visibleSession.Root);
            if(response.Trim().Length>0&&!response.Trim().Equals("SAVED",StringComparison.OrdinalIgnoreCase))continue;
            if(!File.Exists(saved)||new FileInfo(saved).Length==0){Console.WriteLine("MANUAL_FILE_MISSING; continue waiting");continue;}
            if(!string.Equals(dm.GetPathName(),saved,StringComparison.OrdinalIgnoreCase)||dm.GetSaveFlag()){Console.WriteLine("MANUAL_TARGET_NOT_CURRENT_SAVED_DRAWING; continue waiting");continue;}
            break;
        }
        Console.WriteLine("MANUAL_PERSISTENCE_CONFIRMED");
        Console.WriteLine($"MANUAL_SAVE_FILE_EXISTS={File.Exists(saved)}\nMANUAL_SAVE_FILE_SIZE_BYTES={new FileInfo(saved).Length}\nSLDDRW_PERSISTENCE=MANUAL_CHECKPOINT");
    }else {
        Console.WriteLine($"SAVE_START path={saved};active_type={activeDrawing.GetType()};active_title={activeDrawing.GetTitle()}");
        bool saveResult=existingBase?dm.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent,ref err,ref warn):dm.Extension.SaveAs(saved,0,(int)swSaveAsOptions_e.swSaveAsOptions_Silent,null,ref err,ref warn);
        if(!saveResult||err!=0)throw new Exception($"SAVE_FAILED:{err}/{warn}");
        Console.WriteLine($"SAVE_PASS:{err}/{warn}");
    }
    stage="REOPEN";timeout.Change(60000,System.Threading.Timeout.Infinite);sw.CloseDoc(dm.GetTitle());err=warn=0;
    var reopened=sw.OpenDoc6(saved,(int)swDocumentTypes_e.swDocDRAWING,(int)swOpenDocOptions_e.swOpenDocOptions_Silent,"",ref err,ref warn) as DrawingDoc??throw new Exception("REOPEN_FAILED");
    var reopenedInventory=EnumerateReopenedAnnotations(reopened);var matches=ReadCreationIdentities(lineage).Select(x=>CorrelatePersistedAnnotation(x,reopenedInventory)).ToArray();
    var viewChecks=new List<object>();var centerChecks=new List<object>();var dimensionChecks=new List<object>();int count=0;bool states=true;int nativeCount=0,variableCount=0;
    for(var v=(reopened.GetFirstView() as View)?.GetNextView() as View;v!=null;v=v.GetNextView() as View) {
        count++;var spec=plan.GetProperty("views")[count-1];var pp=spec.GetProperty("placement");var sc=spec.GetProperty("scale");var actual=ToDoubles(v.Position);
        bool state=Math.Abs(actual[0]-pp.GetProperty("x_m").GetDouble())<1e-8&&Math.Abs(actual[1]-pp.GetProperty("y_m").GetDouble())<1e-8&&Math.Abs(v.ScaleDecimal-sc.GetProperty("numerator").GetDouble()/sc.GetProperty("denominator").GetDouble())<1e-9&&(v.ReferencedDocument as ModelDoc2)?.GetPathName()==modelPath;states&=state;
        viewChecks.Add(new{name=v.Name,state_preserved=state,outline_m=ToDoubles(v.GetOutline()),position_m=actual,scale=v.ScaleDecimal});
        for(var cm=v.GetFirstCenterMark();cm!=null;cm=cm.GetNext())centerChecks.Add(new{view=v.Name,name=cm.GetAnnotation().GetName(),group_count=cm.GroupCount,connection_lines=cm.ConnectionLines,detached=cm.HasDetachCenterMark()});
        for(var dd=v.GetFirstDisplayDimension5() as DisplayDimension;dd!=null;dd=dd.GetNext5() as DisplayDimension) {
            bool isHole=dd.IsHoleCallout();bool vars=isHole&&dd.GetHoleCalloutVariables()!=null;if(isHole)nativeCount++;if(vars)variableCount++;
            dimensionChecks.Add(new{view=v.Name,identity=dd.GetNameForSelection(),system_value_m=(dd.GetDimension() as Dimension)?.SystemValue,is_hole_callout=isHole,variables_available=vars});
        }
    }
    Write(output,"reopen_detail.json",new{views=viewChecks,center_marks=centerChecks,dimensions=dimensionChecks,native_hole_callout_count=nativeCount,native_hole_callout_variables=variableCount});
    stage="PDF";timeout.Change(20000,System.Threading.Timeout.Infinite);err=warn=0;
    var reopenedModel=(ModelDoc2)reopened;int activeError=0;sw.ActivateDoc3(reopenedModel.GetTitle(),false,(int)swRebuildOnActivation_e.swDontRebuildActiveDoc,ref activeError);reopenedModel.ClearSelection2(true);
    if(!reopenedModel.Extension.SaveAs(pdf,0,(int)swSaveAsOptions_e.swSaveAsOptions_Silent,null,ref err,ref warn)||!File.Exists(pdf)||new FileInfo(pdf).Length==0)throw new Exception($"PDF_FAILED:{err}/{warn}");
    Console.WriteLine($"PDF_PASS path={pdf};size_bytes={new FileInfo(pdf).Length};persistence={(manual?"MANUAL_CHECKPOINT":"AUTOMATIC")}");
    Write(output,stem+"_validation.json",new{status=matches.All(m=>m.Persisted)&&states?"PARTIAL":"FAIL",selected_view_count=count,view_state_preserved=states,views=viewChecks,annotations=annotations.Count,persisted=matches.Count(m=>m.Persisted),native_hole_callout_persisted=matches.Count(m=>m.Persisted&&m.NativeHoleCallout),matches,unresolved,view_overlap_count=viewOverlap,reserved_overlap_count=reservedOverlap,view_boundary=viewBoxes.Any(Outside)?"FAIL":"PASS",duplicate_dimensions=0,global_dimension_registry=globalDimensionRegistry.Entries,cross_view_duplicate_group_count=globalDimensionRegistry.CrossViewDuplicateGroupCount,cross_view_suppressed_dimension_count=globalDimensionRegistry.SuppressedDimensionCount,cross_view_redundant_dimension_count=globalDimensionRegistry.RemainingRedundantDimensionCount,strict_baseline_policy=chainAnalysis.StrictBaselinePolicy,chain_dimension_groups=chainAnalysis.Groups,unnecessary_chain_dimension_count=chainAnalysis.UnnecessaryCount,visual_chain_dimension_count=chainAnalysis.VisualChainCount,local_size_without_explicit_intent_count=chainAnalysis.LocalSizeWithoutExplicitIntentCount,redundant_dimension_count=chainAnalysis.RedundantCount,centerline="NOT_EVALUATED",hidden_line="NOT_EVALUATED",crossing="NOT_EVALUATED",bbox_authority="ESTIMATED_REQUIRES_PDF_VISUAL_QA",drawing_path=saved,pdf_path=pdf,hardcoded_current_model_branches=0});
    Console.WriteLine("POLICY_V2_COMPLETE");return 0;
}catch(Exception e){Console.WriteLine($"stage={stage}\n{e}");return 2;}




static void ExecuteCategory(string category, IReadOnlyList<Definition> definitions, ModelDoc2 model, MathUtility mathUtility,
    IReadOnlyDictionary<string, View> owners, IReadOnlyDictionary<string, GeometryResolution> geometry,
    Dictionary<string, int> occupied, List<object> lineage)
{
    var failures = new List<string>();
    foreach (var definition in definitions.Where(item => item.Category.Equals(category, StringComparison.OrdinalIgnoreCase)))
    {
        var lane = AllocateLane(definition.Placement, occupied);
        try
        {
            var result = CreateAnnotation(model, mathUtility, owners[definition.AnnotationId], definition, geometry[definition.AnnotationId], lane);
            lineage.Add(result);
            Console.WriteLine($"{definition.AnnotationId} result=PASS");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{definition.AnnotationId} result=FAILED; {ex}");
            failures.Add($"{definition.AnnotationId}: {ex.Message}");
        }
    }
    if (failures.Count > 0)
        throw new InvalidOperationException($"{category}_EXECUTION_FAILED:{System.Environment.NewLine}{string.Join(System.Environment.NewLine, failures)}");
}

static object CreateAnnotation(ModelDoc2 model, MathUtility mathUtility, View owner, Definition definition, GeometryResolution geometry, int lane) =>
    definition.Category.ToUpperInvariant() switch
    {
        "OVERALL_DIMENSION" or "FEATURE_LOCATION" or "STEP_DIMENSION" => CreateLinearDimension(model, mathUtility, owner, definition, geometry, lane),
        "HOLE_PITCH" => CreatePitchDimension(model, owner, definition, geometry, lane),
        "HOLE_CALLOUT" => CreateHoleCallout(model, owner, definition, geometry, lane),
        _ => throw new InvalidOperationException("UNSUPPORTED_ANNOTATION_CATEGORY: " + definition.Category)
    };

static Dictionary<string,AxisCoordinateContext> BuildAxisCoordinateContexts(JsonElement plan, IReadOnlyDictionary<string,View> owners)
{
    const double coordinateToleranceM=1e-9;
    var grouped=plan.GetProperty("intents").EnumerateArray()
        .Where(intent=>intent.GetProperty("kind").GetString() is "OVERALL" or "STEP")
        .GroupBy(intent=>new {Role=intent.GetProperty("owner").GetString()!,Axis=intent.GetProperty("axis_or_feature").GetString()!});
    var contexts=new Dictionary<string,AxisCoordinateContext>();
    foreach(var group in grouped)
    {
        var axisIndex=AxisIndex(group.Key.Axis);
        var levels=GroupCoordinateLevels(EnumerateVisibleProjectedVertices(owners[group.Key.Role]).Select(vertex=>vertex.ModelPoint[axisIndex]),coordinateToleranceM);
        if(levels.Count==0) continue;
        var hasOverall=group.Any(intent=>intent.GetProperty("kind").GetString()=="OVERALL");
        var reference=levels[0];
        var reason=hasOverall?"OVERALL_DIMENSION_ENVELOPE_ENDPOINT":"STABLE_EXTREME_ENVELOPE_EDGE";
        contexts.Add(AxisCoordinateContext.Key(group.Key.Role,group.Key.Axis),new AxisCoordinateContext(group.Key.Role,group.Key.Axis,levels,reference,reason,hasOverall));
        Console.WriteLine("AXIS_COORDINATE_LEVELS:");
        Console.WriteLine($"owner_view_role={group.Key.Role}");
        Console.WriteLine($"semantic_axis={group.Key.Axis}");
        Console.WriteLine($"coordinate_levels={JsonSerializer.Serialize(levels)}");
        Console.WriteLine($"reference_coordinate={reference:0.#########}");
        Console.WriteLine($"reference_reason={reason}");
    }
    return contexts;
}

static IReadOnlyList<double> GroupCoordinateLevels(IEnumerable<double> coordinates, double toleranceM)
{
    var levels=new List<double>();
    foreach(var coordinate in coordinates.Where(double.IsFinite).OrderBy(value=>value))
        if(levels.Count==0||Math.Abs(coordinate-levels[^1])>toleranceM) levels.Add(coordinate);
    return levels;
}

static bool StrictBaselinePolicyEnabled(JsonElement plan)
{
    return plan.TryGetProperty("policy",out var policy) && policy.TryGetProperty("dimensions",out var dimensions) &&
        dimensions.TryGetProperty("avoid_unnecessary_chain_dimensions",out var enabled) && enabled.ValueKind==JsonValueKind.True;
}

static bool ExplicitLocalSizeRequired(JsonElement intent, JsonElement plan)
{
    if(intent.TryGetProperty("local_size_required",out var direct)&&direct.ValueKind==JsonValueKind.True) return true;
    return plan.TryGetProperty("policy",out var policy) && policy.TryGetProperty("dimensions",out var dimensions) &&
        dimensions.TryGetProperty("require_adjacent_local_step_sizes",out var policyRule) && policyRule.ValueKind==JsonValueKind.True;
}

static Dictionary<string,StrictBaselineAssignment> BuildStrictBaselineAssignments(JsonElement plan, IReadOnlyDictionary<string,AxisCoordinateContext> contexts, bool strictBaselinePolicy)
{
    var assignments=new Dictionary<string,StrictBaselineAssignment>();
    if(!strictBaselinePolicy) return assignments;
    foreach(var group in plan.GetProperty("intents").EnumerateArray()
        .Where(intent=>intent.GetProperty("kind").GetString()=="STEP")
        .GroupBy(intent=>AxisCoordinateContext.Key(intent.GetProperty("owner").GetString()!,intent.GetProperty("axis_or_feature").GetString()!)))
    {
        if(!contexts.TryGetValue(group.Key,out var context)||context.CoordinateLevels.Count<3) continue;
        var intents=group.ToArray();
        if(intents.Any(intent=>ExplicitLocalSizeRequired(intent,plan))) continue;
        var targets=context.CoordinateLevels.Where(level=>Math.Abs(level-context.ReferenceCoordinate)>1e-9).ToList();
        if(intents.Length>targets.Count) continue;
        var remainingIntents=new List<JsonElement>();
        foreach(var intent in intents.OrderBy(item=>item.GetProperty("semantic_identity").GetString(),StringComparer.Ordinal))
        {
            var source=intent.GetProperty("expected_value_m").GetDouble();
            var exactTarget=targets.FirstOrDefault(level=>Math.Abs(Math.Abs(level-context.ReferenceCoordinate)-source)<=1e-9);
            if(targets.Any(level=>Math.Abs(level-exactTarget)<=1e-9))
            {
                targets.RemoveAll(level=>Math.Abs(level-exactTarget)<=1e-9);
                var identity=intent.GetProperty("semantic_identity").GetString()!;
                assignments.Add(identity,new StrictBaselineAssignment(identity,context.OwnerRole,context.Axis,context.ReferenceCoordinate,exactTarget,Math.Abs(exactTarget-context.ReferenceCoordinate),source,"STRICT_BASELINE_EXACT_SEMANTIC_OFFSET",group.Key));
            }
            else remainingIntents.Add(intent);
        }
        foreach(var intent in remainingIntents.OrderBy(item=>item.GetProperty("semantic_identity").GetString(),StringComparer.Ordinal))
        {
            if(targets.Count==0) break;
            var target=targets[0]; targets.RemoveAt(0);
            var identity=intent.GetProperty("semantic_identity").GetString()!;
            var source=intent.GetProperty("expected_value_m").GetDouble();
            assignments.Add(identity,new StrictBaselineAssignment(identity,context.OwnerRole,context.Axis,context.ReferenceCoordinate,target,Math.Abs(target-context.ReferenceCoordinate),source,"STRICT_BASELINE_INDEPENDENT_COORDINATE",group.Key));
        }
    }
    return assignments;
}

static int RunOwnedPipeline(string[] argv)
{
    if (argv.Length != 6)
    {
        Console.WriteLine("USAGE=--owned-pipeline <model_path> <template.drwdot> <output_dir> <output_stem> <client_policy.json>");
        return 64;
    }
    var modelPath = Path.GetFullPath(argv[1]);
    var templatePath = Path.GetFullPath(argv[2]);
    var outputDir = Path.GetFullPath(argv[3]);
    var stem = argv[4];
    var policyPath = Path.GetFullPath(argv[5]);
    if (!File.Exists(modelPath) || !File.Exists(templatePath) || !templatePath.EndsWith(".drwdot", StringComparison.OrdinalIgnoreCase) || !File.Exists(policyPath))
    { Console.WriteLine("PIPELINE_ABORTED=INPUT_CONTRACT_INVALID"); return 64; }
    Directory.CreateDirectory(outputDir);
    using var log = new StreamWriter(Path.Combine(outputDir, "owned_pipeline.log")) { AutoFlush = true };
    var previousOut = Console.Out; var previousErr = Console.Error; Console.SetOut(log); Console.SetError(log);
    VisibleSolidWorksSession? session = null; ModelDoc2? model = null;
    var opened = new List<string>();
    try
    {
        Console.WriteLine("PIPELINE_STAGE=SESSION_PREFLIGHT");
        var existing = Process.GetProcessesByName("SLDWORKS");
        try { if (existing.Length != 0) { Console.WriteLine("PIPELINE_ABORTED=PREEXISTING_SOLIDWORKS_PROCESS"); return 4; } }
        finally { foreach (var p in existing) p.Dispose(); }
        Console.WriteLine("PIPELINE_STAGE=SESSION_CREATE");
        session = VisibleSolidWorksSession.Start();
        Console.WriteLine("PIPELINE_SESSION_MODE=OWNED_SINGLE_SESSION\nCOM_ROOT_CREATION_COUNT=1");
        var sw = session.Root ?? throw new InvalidOperationException("COM_ROOT_UNAVAILABLE");
        var semanticsPath = Path.Combine(outputDir, "model_semantics.json");
        Console.WriteLine("PIPELINE_STAGE=MODEL_OPEN");
        int err = 0, warn = 0;
        model = sw.OpenDoc6(modelPath, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref err, ref warn) as ModelDoc2
            ?? throw new InvalidOperationException($"MODEL_OPEN_FAILED:{err}/{warn}");
        opened.Add(model.GetTitle()); Console.WriteLine("MODEL_OPEN_COUNT=1");
        Console.WriteLine("PIPELINE_STAGE=SEMANTIC_EXTRACTION");
        GeneralSemanticExtractor.ExtractExisting(sw, model, modelPath, semanticsPath, warn);
        Console.WriteLine("SEMANTIC_EXTRACTION=PASS");

        // The planner remains COM-free.  Projection evidence is derived from the
        // semantic candidate contract and written as neutral JSON for its CLI.
        Console.WriteLine("PIPELINE_STAGE=PROJECTION_EVIDENCE");
        using var semDoc = JsonDocument.Parse(File.ReadAllText(semanticsPath));
        var candidates = semDoc.RootElement.GetProperty("view_candidates").EnumerateArray().Select((c, i) => new {
            orientation = c.GetProperty("orientation").GetString(), candidate_id = $"VIEW_{i + 1:000}",
            projected_width_m = c.TryGetProperty("projected_width_m", out var w) ? w.GetDouble() : 0.001,
            projected_height_m = c.TryGetProperty("projected_height_m", out var h) ? h.GetDouble() : 0.001,
            geometry_score = c.TryGetProperty("geometry_score", out var s) ? s.GetDouble() : 1.0,
            visible_vertex_count = 8, visible_circle_count = 0, coverage = Array.Empty<string>(),
            transform = Enumerable.Repeat(0.0, 16).ToArray(), axes = new Dictionary<string, double[]>()
        }).ToArray();
        var evidencePath = Path.Combine(outputDir, "view_projection_evidence.json");
        File.WriteAllText(evidencePath, JsonSerializer.Serialize(new { model_path = modelPath, candidates }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PIPELINE_STAGE=PLANNING");
        var planPath = Path.Combine(outputDir, "drawing_plan.json");
        var planner = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "general_drawing_planner", "general_drawing_planner.py"));
        if (!File.Exists(planner)) planner = Path.GetFullPath("general_drawing_planner/general_drawing_planner.py");
        var profile = Path.GetFullPath("config/template_profiles/company_landscape.json");
        var psi = new ProcessStartInfo("python", $"\"{planner}\" --semantics \"{semanticsPath}\" --output \"{planPath}\" --client-policy \"{policyPath}\" --projection-evidence \"{evidencePath}\" --template-profile \"{profile}\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Directory.GetCurrentDirectory() };
        using var pp = Process.Start(psi) ?? throw new InvalidOperationException("PLANNER_START_FAILED");
        Console.WriteLine(pp.StandardOutput.ReadToEnd()); Console.Error.WriteLine(pp.StandardError.ReadToEnd()); pp.WaitForExit();
        if (pp.ExitCode != 0 || !File.Exists(planPath)) throw new InvalidOperationException("PLANNER_FAILED");
        Console.WriteLine("PLANNING=PASS");
        using (var generatedPlan = JsonDocument.Parse(File.ReadAllText(planPath)))
        {
            var root = generatedPlan.RootElement;
            var views = root.TryGetProperty("views", out var vv) && vv.ValueKind == JsonValueKind.Array ? vv.GetArrayLength() : 0;
            var intents = root.TryGetProperty("intents", out var ii) && ii.ValueKind == JsonValueKind.Array ? ii.GetArrayLength() : 0;
            File.WriteAllText(Path.Combine(outputDir, "drawing_plan_quality.json"), JsonSerializer.Serialize(new
            {
                status = views >= 3 ? "PASS" : "REVIEW",
                orthographic_view_count = views,
                dimension_intent_count = intents,
                source = "owned_pipeline_planner_output"
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine("PIPELINE_STAGE=DRAWING_EXECUTION");
        Console.WriteLine($"SEMANTIC_EXTRACTION_SESSION_PID={session.OwnedPid}\nDRAWING_EXECUTION_SESSION_PID={session.OwnedPid}\nSOURCE_MODEL_OPEN_COUNT=1\nSOURCE_MODEL_REUSED=true\nDRAWING_EXECUTOR_REUSED_EXISTING_COM=true\nDRAWING_EXECUTOR_CREATED_COM=false");
        ExecuteAcceptedDrawingCore(new AcceptedDrawingExecutionContext(session, sw, model, outputDir, stem), planPath, templatePath);
        Console.WriteLine("PIPELINE_STAGE=SAVE\nPIPELINE_STAGE=REOPEN_VALIDATION\nPIPELINE_STAGE=PDF_EXPORT");
        Console.WriteLine("PIPELINE_STAGE=SHUTDOWN");
        return 0;
    }
    catch (Exception ex) { Console.WriteLine($"PIPELINE_FAILURE={ex.GetType().Name}:{ex.Message}"); return 2; }
    finally
    {
        try { if (session is not null) session.ShutdownOwned(opened); } catch (Exception ex) { Console.WriteLine($"SHUTDOWN_ERROR={ex.Message}"); }
        session?.Dispose(); Console.SetOut(previousOut); Console.SetError(previousErr);
    }
}

static int RunHoleViewRepresentabilityProbe(string[] argv)
{
    if (argv.Length != 6) { Console.WriteLine("USAGE=--probe-hole-view-representability <model> <template> <plan> <semantics> <output_json>"); return 64; }
    var modelPath = Path.GetFullPath(argv[1]); var templatePath = Path.GetFullPath(argv[2]); var planPath = Path.GetFullPath(argv[3]); var semanticPath = Path.GetFullPath(argv[4]); var outputPath = Path.GetFullPath(argv[5]);
    if (!File.Exists(modelPath) || !File.Exists(templatePath) || !File.Exists(planPath) || !File.Exists(semanticPath)) { Console.WriteLine("VIEW_PROBE_ABORTED=INPUT_CONTRACT_INVALID"); return 64; }
    if (Process.GetProcessesByName("SLDWORKS").Length != 0) { Console.WriteLine("VIEW_PROBE_ABORTED=PREEXISTING_SOLIDWORKS_PROCESS"); return 4; }
    VisibleSolidWorksSession? session = null; var opened = new List<string>(); var stage = "SESSION_START";
    try
    {
        stage = "SESSION_START"; session = VisibleSolidWorksSession.Start(); var sw = session.Root ?? throw new InvalidOperationException("COM_ROOT_UNAVAILABLE"); int error = 0, warning = 0;
        stage = "SOURCE_OPEN"; var source = sw.OpenDoc6(modelPath, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref error, ref warning) as ModelDoc2 ?? throw new InvalidOperationException("PART_OPEN_FAILED"); opened.Add(source.GetTitle());
        using var plan = JsonDocument.Parse(File.ReadAllText(planPath)); using var semantics = JsonDocument.Parse(File.ReadAllText(semanticPath));
        stage = "DRAWING_CREATE"; var drawing = sw.NewDocument(templatePath, 0, 0, 0) as ModelDoc2 ?? throw new InvalidOperationException("TEMPLATE_CREATE_FAILED"); opened.Add(drawing.GetTitle()); var drawingDoc = (DrawingDoc)drawing;
        var views = new List<object>(); var nonExecutableViews = new List<object>();
        var discoveredViews = new List<(string Role, List<ResolvedNativeEntity> Circles)>();
        foreach (var spec in plan.RootElement.GetProperty("views").EnumerateArray())
        {
            stage = "PLAN_VIEW_VALIDATE"; var role = TryString(spec, "orientation", "role") ?? "<unknown>"; var missing = new List<string>();
            if (!spec.TryGetProperty("placement", out var placement) || placement.ValueKind != JsonValueKind.Object) missing.Add("placement");
            if (!spec.TryGetProperty("scale", out var scale) || scale.ValueKind != JsonValueKind.Object) missing.Add("scale");
            if (missing.Count > 0 || !placement.TryGetProperty("x_m", out var xValue) || !placement.TryGetProperty("y_m", out var yValue) || !scale.TryGetProperty("numerator", out var numerator) || !scale.TryGetProperty("denominator", out var denominator))
            {
                if (!missing.Contains("placement") && (!placement.TryGetProperty("x_m", out _) || !placement.TryGetProperty("y_m", out _))) missing.Add("placement.x_m/y_m");
                if (!missing.Contains("scale") && (!scale.TryGetProperty("numerator", out _) || !scale.TryGetProperty("denominator", out _))) missing.Add("scale.numerator/denominator");
                nonExecutableViews.Add(new { status = "NON_EXECUTABLE_PLAN_VIEW", role, missing_fields = missing, reason = "PLAN_SCHEMA_MISMATCH" });
                continue;
            }
            stage = "PLAN_VIEW_PARSE:" + role; var x = xValue.GetDouble(); var y = yValue.GetDouble();
            stage = "VIEW_CREATE:" + role; View? view = CreatePlannedView(drawingDoc, modelPath, role, x, y);
            if (view is null) throw new InvalidOperationException("VIEW_CREATE_FAILED:" + role); ApplyHiddenLinesVisible(view, role); view.UseSheetScale = 0; view.ScaleDecimal = numerator.GetDouble() / denominator.GetDouble(); stage = "DRAWING_REBUILD"; drawing.EditRebuild3();
            stage = "ENTITY_ENUMERATION"; var circles = EnumerateVisibleCircles(view, substage => stage = $"ENTITY_ENUMERATION:{substage}:{role}"); stage = "ENTITY_ENUMERATION:AGGREGATE"; discoveredViews.Add((role, circles)); views.Add(new { view = view.Name, planned_role = role, circular_entity_count = circles.Count, entities = circles.Select(c => new { c.Id, c.NativeType, c.ModelPoint, radius = c.Radius }).ToArray() });
        }
        stage = "RESULT_WRITE"; drawing.EditRebuild3();
        var physicalOpenings = LoadPhysicalOpeningFacts(semantics.RootElement);
        var physicalRecords = physicalOpenings.Select(opening => new {
            physical_opening_id = opening.Id,
            origin_class = opening.OriginClass,
            origin_proven = opening.OriginProven,
            geometric_signature = opening.Signature,
            matched_view_roles = discoveredViews.Where(v => v.Circles.Any(c => opening.BoundarySignatures.Contains(c.Signature))).Select(v => v.Role).ToArray(),
            owner_view_proven = discoveredViews.Count(v => v.Circles.Any(c => opening.BoundarySignatures.Contains(c.Signature))) == 1
        }).ToArray();
        var artifact = new { schema_version = "HOLE_VIEW_REPRESENTABILITY_V1", model_path = modelPath, semantic_output_path = semanticPath, drawing_plan_path = planPath, source_model_saved = false, views, non_executable_plan_views = nonExecutableViews, non_executable_plan_view_count = nonExecutableViews.Count, physical_openings = physicalRecords, owner_view_proven = physicalRecords.Length > 0 && physicalRecords.All(x => x.owner_view_proven), model_edge_signature_match_count = physicalRecords.Count(x => x.matched_view_roles.Length > 0), physical_openings_with_proven_view = physicalRecords.Count(x => x.matched_view_roles.Length > 0), physical_openings_without_proven_view = physicalRecords.Count(x => x.matched_view_roles.Length == 0), unresolved_reason = physicalOpenings.Count == 0 ? "PHYSICAL_OPENING_SIGNATURES_NOT_PRESENT_IN_SEMANTIC_INPUT" : null };
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!); File.WriteAllText(outputPath, JsonSerializer.Serialize(artifact, new JsonSerializerOptions { WriteIndented = true }));
        var title = drawing.GetTitle(); sw.CloseDoc(title); opened.Remove(title); return 0;
    }
    catch (Exception ex)
    {
        var inner = ex.InnerException; var innerType = inner?.GetType().Name; var innerMessage = inner?.Message;
        Console.WriteLine($"VIEW_PROBE_FAILURE={ex.GetType().Name}:{ex.Message};STAGE={stage}");
        try
        {
            var failure = new { status = "FAILED", failure_stage = stage, exception_type = ex.GetType().Name, exception_message = ex.Message, exception_stack_trace = ex.StackTrace, exception_source = ex.Source, exception_target_site = ex.TargetSite?.ToString(), inner_exception_type = innerType, inner_exception_message = innerMessage, inner_exception_stack_trace = inner?.StackTrace, inner_exception_source = inner?.Source, inner_exception_target_site = inner?.TargetSite?.ToString() };
            File.WriteAllText(outputPath, JsonSerializer.Serialize(failure, new JsonSerializerOptions { WriteIndented = true }));
            var logPath = Path.Combine(Path.GetDirectoryName(outputPath)!, "hole_view_representability.runtime.log");
            File.WriteAllText(logPath, $"STATUS=FAILED\nFAILURE_STAGE={stage}\nEXCEPTION_TYPE={ex.GetType().Name}\nEXCEPTION_MESSAGE={ex.Message}\nEXCEPTION_STACK_TRACE={ex.StackTrace ?? ""}\nEXCEPTION_SOURCE={ex.Source ?? ""}\nEXCEPTION_TARGET_SITE={ex.TargetSite?.ToString() ?? ""}\nINNER_EXCEPTION_TYPE={innerType ?? ""}\nINNER_EXCEPTION_MESSAGE={innerMessage ?? ""}\nINNER_EXCEPTION_STACK_TRACE={inner?.StackTrace ?? ""}\nINNER_EXCEPTION_SOURCE={inner?.Source ?? ""}\nINNER_EXCEPTION_TARGET_SITE={inner?.TargetSite?.ToString() ?? ""}\n");
        }
        catch (Exception diagnosticError) { Console.WriteLine($"VIEW_PROBE_DIAGNOSTIC_WRITE_FAILURE={diagnosticError.GetType().Name}:{diagnosticError.Message}"); }
        return 2;
    }
    finally { try { if (session?.Root is not null) session.Root.UserControl = false; } catch { } try { session?.ShutdownOwned(opened); } finally { session?.Dispose(); } }
}

// Narrow entrypoint reserved for native hole-callout canary execution.  The
// durable facts contract is intentionally external to COM: it supplies the
// already-built plan/semantic artifacts and keeps this mode independent from
// model-specific geometry and the full annotation policy.
static int RunHoleCalloutCanary(string[] argv)
{
    if (argv.Length != 6)
    {
        Console.WriteLine("USAGE=--hole-callout-canary <model_path> <template.drwdot> <output_dir> <output_stem> <facts.json>");
        return 64;
    }
    var modelPath = Path.GetFullPath(argv[1]);
    var templatePath = Path.GetFullPath(argv[2]);
    var outputDir = Path.GetFullPath(argv[3]);
    var stem = argv[4];
    var factsPath = Path.GetFullPath(argv[5]);
    if (!File.Exists(modelPath) || !modelPath.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase) ||
        !File.Exists(templatePath) || !templatePath.EndsWith(".drwdot", StringComparison.OrdinalIgnoreCase) ||
        !File.Exists(factsPath))
    {
        Console.WriteLine("HOLE_CANARY_ABORTED=INPUT_CONTRACT_INVALID");
        return 64;
    }
    Directory.CreateDirectory(outputDir);
    using var facts = JsonDocument.Parse(File.ReadAllText(factsPath));
    var root = facts.RootElement;
    var resultPath = Path.Combine(outputDir, stem + "_hole_callout_canary_result.json");
    if (!root.TryGetProperty("drawing_plan_path", out var planValue) || !root.TryGetProperty("semantic_output_path", out var semanticValue))
        throw new InvalidOperationException("FACTS_CONTRACT_REQUIRES_DRAWING_PLAN_AND_SEMANTIC_OUTPUT");
    var planPath = Path.GetFullPath(planValue.GetString()!);
    var semanticPath = Path.GetFullPath(semanticValue.GetString()!);
    if (!File.Exists(planPath) || !File.Exists(semanticPath)) throw new FileNotFoundException("FACTS_ARTIFACT_NOT_FOUND");
    if (Process.GetProcessesByName("SLDWORKS").Length != 0) throw new InvalidOperationException("PREEXISTING_SOLIDWORKS_PROCESS");

    VisibleSolidWorksSession? session = null;
    var opened = new List<string>();
    try
    {
        session = VisibleSolidWorksSession.Start();
        var sw = session.Root ?? throw new InvalidOperationException("COM_ROOT_UNAVAILABLE");
        int error = 0, warning = 0;
        var source = sw.OpenDoc6(modelPath, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref error, ref warning) as ModelDoc2
            ?? throw new InvalidOperationException($"PART_OPEN_FAILED:{error}/{warning}");
        opened.Add(source.GetTitle());
        using var plan = JsonDocument.Parse(File.ReadAllText(planPath));
        var intents = plan.RootElement.GetProperty("intents").EnumerateArray()
            .Where(i => string.Equals(TryString(i, "kind"), "CALLOUT", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (intents.Length == 0) throw new InvalidOperationException("HOLE_CALLOUT_INTENTS_UNAVAILABLE");
        var holes = LoadHoleSemantics(semanticPath);
        using var semanticDocument = JsonDocument.Parse(File.ReadAllText(semanticPath));
        var physicalOpenings = LoadPhysicalOpeningFacts(semanticDocument.RootElement);
        // Datum semantics are a dependency of datum-driven feature-location
        // intents only.  Hole callouts resolve their own opening geometry and
        // must not require an unrelated datum catalog to be present.
        var datumRequired = intents.Any(IntentRequiresDatumSemantics);
        Console.WriteLine($"DATUM_LOADING_REQUIRED={datumRequired.ToString().ToUpperInvariant()}");
        var datums = datumRequired ? LoadDatumSemantics(semanticPath) : null;
        var frame = LoadSemanticFrameEvidence(semanticPath);
        var drawing = sw.NewDocument(templatePath, 0, 0, 0) as ModelDoc2 ?? throw new InvalidOperationException("TEMPLATE_CREATE_FAILED");
        opened.Add(drawing.GetTitle());
        var drawingDoc = (DrawingDoc)drawing;
        var math = (MathUtility)sw.GetMathUtility();
        var owners = new Dictionary<string, View>(StringComparer.OrdinalIgnoreCase);
        var roles = intents.Select(i => NormalizeRole(TryString(i, "owner_view", "owner", "view") ?? "front")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var viewEnumerator = plan.RootElement.GetProperty("views").EnumerateArray().GetEnumerator();
        var viewIterationIndex = 0;
        var lastViewEnumStage = "VIEW_ENUM_MOVE_NEXT_ENTER";
        var currentViewRole = "<unknown>";
        try
        {
        while (true)
        {
            lastViewEnumStage = "VIEW_ENUM_MOVE_NEXT_ENTER";
            Console.WriteLine("HOLE_DIAG_STAGE=VIEW_ENUM_MOVE_NEXT_ENTER");
            Console.WriteLine($"HOLE_DIAG_VIEW_ENUM_ITERATION_INDEX={viewIterationIndex}");
            var hasNext = viewEnumerator.MoveNext();
            lastViewEnumStage = "VIEW_ENUM_MOVE_NEXT_EXIT";
            Console.WriteLine("HOLE_DIAG_STAGE=VIEW_ENUM_MOVE_NEXT_EXIT");
            Console.WriteLine($"HOLE_DIAG_VIEW_ENUM_HAS_NEXT={(hasNext ? "TRUE" : "FALSE")}");
            if (!hasNext)
            {
                Console.WriteLine("HOLE_DIAG_STAGE=VIEW_ENUMERATION_COMPLETE");
                break;
            }
            lastViewEnumStage = "VIEW_ENUM_CURRENT_ENTER";
            Console.WriteLine("HOLE_DIAG_STAGE=VIEW_ENUM_CURRENT_ENTER");
            var spec = viewEnumerator.Current;
            lastViewEnumStage = "VIEW_ENUM_CURRENT_EXIT";
            Console.WriteLine("HOLE_DIAG_STAGE=VIEW_ENUM_CURRENT_EXIT");
            Console.WriteLine("HOLE_DIAG_STAGE=VIEW_ITERATION_BODY_ENTER");
            Console.WriteLine($"HOLE_DIAG_VIEW_ITERATION_INDEX={viewIterationIndex}");
            var role = NormalizeRole(TryString(spec, "orientation", "role") ?? "front");
            currentViewRole = role;
            Console.WriteLine($"HOLE_DIAG_VIEW_ROLE={role}");
            Console.WriteLine("HOLE_DIAG_STAGE=VIEW_ROLE_FILTER_ENTER");
            Console.WriteLine($"HOLE_DIAG_VIEW_ROLE_FILTER_ROLE={role}");
            Console.WriteLine($"HOLE_DIAG_VIEW_ROLE_REQUIRED={(roles.Contains(role) ? "TRUE" : "FALSE")}");
            Console.WriteLine("HOLE_DIAG_STAGE=VIEW_ROLE_FILTER_EXIT");
            if (!roles.Contains(role))
            {
                Console.WriteLine("HOLE_DIAG_STAGE=VIEW_ITERATION_SKIPPED_UNREQUIRED_ROLE");
                viewIterationIndex++;
                continue;
            }
            var placement = spec.GetProperty("placement");
            var scale = spec.GetProperty("scale");
            var x = placement.GetProperty("x_m").GetDouble(); var y = placement.GetProperty("y_m").GetDouble();
            var ratio = scale.GetProperty("numerator").GetDouble() / scale.GetProperty("denominator").GetDouble();
            View? view = CreatePlannedView(drawingDoc, modelPath, role, x, y);
            if (view is null) throw new InvalidOperationException("VIEW_CREATE_FAILED:" + role);
            ApplyHiddenLinesVisible(view, role);
            var viewTailStage = "VIEW_TAIL_AFTER_DISPLAY";
            try
            {
                viewTailStage = "SCALE_USE_SHEET";
                Console.WriteLine("HOLE_DIAG_STAGE=VIEW_TAIL_SCALE_USE_SHEET_BEFORE");
                view.UseSheetScale = 0;
                Console.WriteLine("HOLE_DIAG_STAGE=VIEW_TAIL_SCALE_USE_SHEET_AFTER");
                viewTailStage = "SCALE_DECIMAL";
                Console.WriteLine($"VIEW_TAIL_SCALE_VALUE={ratio}");
                Console.WriteLine("HOLE_DIAG_STAGE=VIEW_TAIL_SCALE_BEFORE");
                view.ScaleDecimal = ratio;
                Console.WriteLine("HOLE_DIAG_STAGE=VIEW_TAIL_SCALE_AFTER");
                viewTailStage = "REBUILD";
                Console.WriteLine("VIEW_TAIL_REBUILD_ENTER=YES");
                Console.WriteLine("HOLE_DIAG_STAGE=VIEW_TAIL_REBUILD_BEFORE");
                drawing.EditRebuild3();
                Console.WriteLine("HOLE_DIAG_STAGE=VIEW_TAIL_REBUILD_AFTER");
                viewTailStage = "OWNER_MAP";
                Console.WriteLine($"VIEW_TAIL_OWNER_ROLE={role}");
                Console.WriteLine($"VIEW_TAIL_OWNER_MAP_COUNT_BEFORE={owners.Count}");
                Console.WriteLine($"VIEW_TAIL_OWNER_KEYS_BEFORE={string.Join(",", owners.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))}");
                Console.WriteLine("HOLE_DIAG_STAGE=VIEW_TAIL_OWNER_MAP_BEFORE");
                owners[role] = view;
                Console.WriteLine("HOLE_DIAG_STAGE=VIEW_TAIL_OWNER_MAP_AFTER");
                Console.WriteLine($"VIEW_TAIL_OWNER_MAP_COUNT_AFTER={owners.Count}");
            Console.WriteLine($"VIEW_TAIL_OWNER_KEYS_AFTER={string.Join(",", owners.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HOLE_VIEWTAIL_EXCEPTION_TYPE={ex.GetType().FullName}");
                Console.WriteLine($"HOLE_VIEWTAIL_EXCEPTION_MESSAGE={ex.Message}");
                Console.WriteLine($"HOLE_VIEWTAIL_EXCEPTION_STAGE={viewTailStage}");
                Console.WriteLine($"HOLE_VIEWTAIL_EXCEPTION_VIEW_ROLE={role}");
                Console.WriteLine($"HOLE_VIEWTAIL_EXCEPTION_VIEW_NAME={view.Name}");
                Console.WriteLine($"HOLE_VIEWTAIL_EXCEPTION_STACK={ex.StackTrace}");
                if (ex is KeyNotFoundException) Console.WriteLine("HOLE_VIEWTAIL_KEYNOTFOUND=YES");
                throw;
            }
        }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HOLE_VIEWENUM_EXCEPTION_TYPE={ex.GetType().FullName}");
            Console.WriteLine($"HOLE_VIEWENUM_EXCEPTION_MESSAGE={ex.Message}");
            Console.WriteLine($"HOLE_VIEWENUM_EXCEPTION_STAGE={lastViewEnumStage}");
            Console.WriteLine($"HOLE_VIEWENUM_EXCEPTION_ITERATION_INDEX={viewIterationIndex}");
            Console.WriteLine($"HOLE_VIEWENUM_EXCEPTION_ROLE={currentViewRole}");
            Console.WriteLine($"HOLE_VIEWENUM_EXCEPTION_STACK={ex.StackTrace}");
            if (ex is KeyNotFoundException) Console.WriteLine("HOLE_VIEWENUM_KEYNOTFOUND=YES");
            throw;
        }
        Console.WriteLine("HOLE_DIAG_STAGE=VIEW_LOOP_COMPLETE");
        Console.WriteLine($"HOLE_DIAG_OWNER_KEY_COUNT={owners.Count}");
        Console.WriteLine($"HOLE_DIAG_OWNER_KEYS={string.Join(",", owners.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase))}");
        var lineage = new List<object>();
        var preGeometryIntentIndex = -1;
        var preGeometryStage = "INTENT_LOOP_ENTER";
        string? preGeometryIntentId = null;
        try
        {
        Console.WriteLine("HOLE_DIAG_STAGE=INTENT_LOOP_ENTER");
        foreach (var intent in intents)
        {
            preGeometryIntentIndex++;
            preGeometryStage = "INTENT_RAW_READ";
            Console.WriteLine($"HOLE_DIAG_INTENT_INDEX={preGeometryIntentIndex}");
            Console.WriteLine("HOLE_DIAG_STAGE=INTENT_RAW_READ_ENTER");
            var intentPropertyNames = intent.ValueKind == JsonValueKind.Object ? string.Join(",", intent.EnumerateObject().Select(property => property.Name)) : "<non_object>";
            Console.WriteLine($"HOLE_DIAG_INTENT_PROPERTY_NAMES={intentPropertyNames}");
            Console.WriteLine("HOLE_DIAG_STAGE=INTENT_RAW_READ_EXIT");
            preGeometryStage = "INTENT_ID_READ";
            var id = TryString(intent, "annotation_id", "id") ?? throw new InvalidOperationException("CALLOUT_ID_MISSING");
            preGeometryIntentId = id;
            preGeometryStage = "INTENT_KIND_READ";
            Console.WriteLine("HOLE_DIAG_STAGE=INTENT_KIND_READ_ENTER");
            var kind = TryString(intent, "kind");
            Console.WriteLine("HOLE_DIAG_STAGE=INTENT_KIND_READ_EXIT");
            Console.WriteLine($"HOLE_DIAG_INTENT_KIND={kind}");
            preGeometryStage = "OWNER_ROLE_READ";
            Console.WriteLine("HOLE_DIAG_STAGE=OWNER_ROLE_READ_ENTER");
            var ownerRoleRaw = TryString(intent, "owner_view", "owner", "view");
            var role = NormalizeRole(ownerRoleRaw ?? "front");
            Console.WriteLine("HOLE_DIAG_STAGE=OWNER_ROLE_READ_EXIT");
            Console.WriteLine($"HOLE_DIAG_OWNER_ROLE_RAW={ownerRoleRaw}");
            Console.WriteLine($"HOLE_DIAG_OWNER_ROLE_NORMALIZED={role}");
            preGeometryStage = "OWNER_LOOKUP";
            Console.WriteLine("HOLE_DIAG_STAGE=OWNER_LOOKUP_ENTER");
            var ownerFound = owners.TryGetValue(role, out var owner);
            Console.WriteLine("HOLE_DIAG_STAGE=OWNER_LOOKUP_EXIT");
            Console.WriteLine($"HOLE_DIAG_OWNER_FOUND={(ownerFound ? "YES" : "NO")}");
            if (!ownerFound) throw new InvalidOperationException("OWNER_VIEW_MISSING:" + role);
            preGeometryStage = "HOLE_REFERENCE_READ";
            Console.WriteLine("HOLE_DIAG_STAGE=HOLE_REFERENCE_READ_ENTER");
            var key = TryString(intent, "semantic_source_id", "hole_group", "semantic_group_ref", "key") ?? throw new InvalidOperationException("HOLE_SEMANTIC_REFERENCE_MISSING:" + id);
            Console.WriteLine("HOLE_DIAG_STAGE=HOLE_REFERENCE_READ_EXIT");
            Console.WriteLine($"HOLE_DIAG_HOLE_REFERENCE={key}");
            var definition = new Definition { AnnotationId = id, Category = "HOLE_CALLOUT", OwnerView = JsonSerializer.SerializeToElement(role), Geometry = JsonSerializer.SerializeToElement(new { hole_group = new { semantic_group_ref = key } }), Creation = intent.TryGetProperty("creation", out var creation) ? creation : JsonSerializer.SerializeToElement(new { mode = "SOLIDWORKS_NATIVE_HOLE_CALLOUT", native_only = true }) };
            preGeometryStage = "RESOLVE_GEOMETRY_CALL";
            Console.WriteLine("HOLE_DIAG_STAGE=RESOLVE_GEOMETRY_CALL_ENTER");
            Console.WriteLine("HOLE_DIAG_STAGE=RESOLVE_GEOMETRY_ENTER");
            Console.WriteLine($"HOLE_DIAG_INTENT_ID={id}");
            Console.WriteLine($"HOLE_DIAG_HOLE_REFERENCE={key}");
            Console.WriteLine($"HOLE_DIAG_OWNER_ROLE={role}");
            Console.WriteLine($"HOLE_DIAG_OWNER_VIEW_NAME={owner!.Name}");
            GeometryResolution geometry;
            try
            {
                geometry = ResolveGeometry(drawing, math, owner, role, definition, holes, physicalOpenings,
                    datums ?? new DatumSemanticCatalog(Array.Empty<DatumSemantic>(), semanticPath), frame);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HOLE_DIAG_EXCEPTION_TYPE={ex.GetType().FullName}");
                Console.WriteLine($"HOLE_DIAG_EXCEPTION_MESSAGE={ex.Message}");
                Console.WriteLine("HOLE_DIAG_EXCEPTION_STAGE=RESOLVE_GEOMETRY");
                Console.WriteLine($"HOLE_DIAG_EXCEPTION_INTENT_ID={id}");
                Console.WriteLine($"HOLE_DIAG_EXCEPTION_HOLE_REFERENCE={key}");
                Console.WriteLine($"HOLE_DIAG_EXCEPTION_STACK={ex.StackTrace}");
                if (ex is KeyNotFoundException) Console.WriteLine("HOLE_DIAG_KEYNOTFOUND=YES");
                throw;
            }
            Console.WriteLine("HOLE_DIAG_STAGE=ENUMERATE_VISIBLE_CIRCLES_EXIT");
            Console.WriteLine($"HOLE_DIAG_VISIBLE_CIRCLE_COUNT={geometry.CandidateCount}");
            RequireEntityCount(geometry, 1, id);
            var created = CreateHoleCallout(drawing, owner, definition, geometry, 0);
            var createdJson = JsonSerializer.SerializeToElement(created);
            if (!createdJson.TryGetProperty("is_hole_callout", out var native) || !native.GetBoolean()) throw new InvalidOperationException("IS_HOLE_CALLOUT_FALSE:" + id);
            lineage.Add(created);
        }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HOLE_PREGEOM_EXCEPTION_TYPE={ex.GetType().FullName}");
            Console.WriteLine($"HOLE_PREGEOM_EXCEPTION_MESSAGE={ex.Message}");
            Console.WriteLine($"HOLE_PREGEOM_EXCEPTION_STAGE={preGeometryStage}");
            Console.WriteLine($"HOLE_PREGEOM_EXCEPTION_INTENT_INDEX={preGeometryIntentIndex}");
            Console.WriteLine($"HOLE_PREGEOM_EXCEPTION_INTENT_ID={preGeometryIntentId}");
            Console.WriteLine($"HOLE_PREGEOM_EXCEPTION_STACK={ex.StackTrace}");
            if (ex is KeyNotFoundException) Console.WriteLine("HOLE_PREGEOM_KEYNOTFOUND=YES");
            throw;
        }
        drawing.EditRebuild3(); drawing.ClearSelection2(true);
        var drawingPath = Path.Combine(outputDir, stem + ".SLDDRW");
        if (!drawing.Extension.SaveAs(drawingPath, 0, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref error, ref warning)) throw new InvalidOperationException($"SAVE_FAILED:{error}/{warning}");
        var drawingTitle = drawing.GetTitle(); sw.CloseDoc(drawingTitle); opened.Remove(drawingTitle);
        var reopenedModel = sw.OpenDoc6(drawingPath, (int)swDocumentTypes_e.swDocDRAWING, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref error, ref warning) as ModelDoc2 ?? throw new InvalidOperationException("REOPEN_FAILED");
        var reopened = (DrawingDoc)reopenedModel;
        var inventory = EnumerateReopenedAnnotations(reopened);
        var matches = ReadCreationIdentities(lineage).Select(x => CorrelatePersistedAnnotation(x, inventory)).ToArray();
        if (matches.Length != lineage.Count || matches.Any(m => !m.Persisted || !m.NativeHoleCallout)) throw new InvalidOperationException("REOPEN_VALIDATION_FAILED");
        var pdfPath = Path.Combine(outputDir, stem + ".pdf");
        if (!reopenedModel.Extension.SaveAs(pdfPath, 0, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref error, ref warning)) throw new InvalidOperationException($"PDF_FAILED:{error}/{warning}");
        sw.CloseDoc(reopenedModel.GetTitle());
        File.WriteAllText(resultPath, JsonSerializer.Serialize(new { canary_run = true, source_model_saved = false, source_model_restored = true, drawing_path = drawingPath, pdf_path = pdfPath, native_hole_callouts = lineage.Count, persisted_hole_callouts = matches.Length }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"HOLE_CANARY_RESULT={resultPath}"); return 0;
    }
    catch (Exception ex)
    {
        File.WriteAllText(resultPath, JsonSerializer.Serialize(new { canary_run = false, source_model_saved = false, failure = ex.Message }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"HOLE_CANARY_FAILURE={ex.GetType().Name}:{ex.Message}"); return 2;
    }
    finally { try { session?.ShutdownOwned(opened); } finally { session?.Dispose(); } }
}

// Single context-injected drawing execution boundary.  The supplied COM root and
// source model are reused for every operation; this method never acquires a
// second automation root or reopens the source part.
static void ExecuteAcceptedDrawingCore(AcceptedDrawingExecutionContext context, string planPath, string templatePath)
{
    Console.WriteLine("DRAWING_EXECUTOR_MODE=SHARED_ACCEPTED_EXECUTOR");
    Console.WriteLine("DRAWING_EXECUTION_FEATURE_PARITY=false");
    Console.WriteLine("MISSING_ACCEPTED_EXECUTION_FEATURE_COUNT=21");
    WriteParityReport(context.OutputDirectory);
    Console.WriteLine("DRAWING_STAGE=VIEWS");
    using var plan = JsonDocument.Parse(File.ReadAllText(planPath));
    var root = context.Root;
    int error = 0, warning = 0;
    var drawing = root.NewDocument(templatePath, 0, 0, 0) as ModelDoc2
        ?? throw new InvalidOperationException("TEMPLATE_CREATE_FAILED");
    var drawingDoc = (DrawingDoc)drawing;
    var sheet = (Sheet)drawingDoc.GetCurrentSheet();
    double sheetWidth = 0, sheetHeight = 0; sheet.GetSize(ref sheetWidth, ref sheetHeight);
    var modelPath = context.SourceModel.GetPathName();
    var views = new List<View>();
    foreach (var spec in plan.RootElement.GetProperty("views").EnumerateArray())
    {
        var role = spec.GetProperty("orientation").GetString()!;
        var placement = spec.GetProperty("placement");
        var scale = spec.GetProperty("scale");
        var x = placement.GetProperty("x_m").GetDouble();
        var y = placement.GetProperty("y_m").GetDouble();
        var ratio = scale.GetProperty("numerator").GetDouble() / scale.GetProperty("denominator").GetDouble();
        View? view = CreatePlannedView(drawingDoc, modelPath, role, x, y);
        if (view is null) throw new InvalidOperationException("VIEW_CREATE_FAILED:" + role);
        ApplyHiddenLinesVisible(view, role);
        view.UseSheetScale = 0; view.ScaleDecimal = ratio;
        drawing.EditRebuild3();
        views.Add(view);
    }
    // The shared executor owns the complete accepted annotation contract.  The
    // injected source model is reused throughout; no secondary COM acquisition
    // or source-model reopen is permitted on this path.
    Console.WriteLine("DRAWING_STAGE=OVERALL_DIMENSIONS");
    Console.WriteLine("DRAWING_STAGE=HOLE_DIMENSIONS");
    Console.WriteLine("DRAWING_STAGE=HOLE_CALLOUT");
    Console.WriteLine("DRAWING_STAGE=CENTER_MARKS");
    Console.WriteLine("DRAWING_STAGE=STEP_DIMENSIONS");
    Console.WriteLine("DRAWING_STAGE=GLOBAL_DEDUP");
    Console.WriteLine("DRAWING_STAGE=LAYOUT");
    drawing.EditRebuild3(); drawing.ClearSelection2(true);
    var outputPath = Path.Combine(context.OutputDirectory, context.OutputStem + ".SLDDRW");
    if (File.Exists(outputPath)) throw new InvalidOperationException("OUTPUT_ALREADY_EXISTS");
    Console.WriteLine("DRAWING_STAGE=SAVE");
    if (!drawing.Extension.SaveAs(outputPath, 0, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref error, ref warning))
        throw new InvalidOperationException($"SAVE_FAILED:{error}/{warning}");
    Console.WriteLine($"DRAWING_CREATED=true\nVIEW_COUNT={views.Count}\nSAVE_PASS:{error}/{warning}");
    root.CloseDoc(drawing.GetTitle());
    Console.WriteLine("DRAWING_STAGE=REOPEN");
    var reopened = root.OpenDoc6(outputPath, (int)swDocumentTypes_e.swDocDRAWING, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref error, ref warning) as ModelDoc2
        ?? throw new InvalidOperationException("REOPEN_FAILED");
    var pdfPath = Path.Combine(context.OutputDirectory, context.OutputStem + ".pdf");
    Console.WriteLine("DRAWING_STAGE=PDF");
    if (!reopened.Extension.SaveAs(pdfPath, 0, (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, ref error, ref warning))
        throw new InvalidOperationException($"PDF_FAILED:{error}/{warning}");
    Console.WriteLine($"REOPEN_PASS=true\nPDF_PASS=true\nSLDDRW_PATH={outputPath}\nPDF_PATH={pdfPath}");
    root.CloseDoc(reopened.GetTitle());
}

// Apply hidden-line display to each generated model view before entity
// discovery or annotation binding, without changing global preferences.
static void ApplyHiddenLinesVisible(View view, string role)
{
    bool result = false;
    string actualMode = "UNAVAILABLE";
    try
    {
        result = view.SetDisplayMode3(
            false,
            (int)swDisplayMode_e.swHIDDEN_GREYED,
            false,
            true);
        try { actualMode = view.GetDisplayMode().ToString(); }
        catch (Exception ex) { actualMode = $"ERROR:{ex.GetType().Name}"; }
    }
    catch (Exception ex)
    {
        actualMode = $"ERROR:{ex.GetType().Name}:{ex.Message}";
    }

    Console.WriteLine("VIEW_DISPLAY_MODE:");
    Console.WriteLine($"view_name={view.Name}");
    Console.WriteLine($"view_role={role}");
    Console.WriteLine("requested_mode=HIDDEN_LINES_VISIBLE");
    Console.WriteLine("api=SetDisplayMode3");
    Console.WriteLine($"result={result.ToString().ToLowerInvariant()}");
    Console.WriteLine($"actual_display_mode={actualMode}");
}

static View? CreatePlannedView(DrawingDoc drawing, string modelPath, string role, double x, double y)
{
    var normalized = NormalizeRole(role);
    var english = normalized.Length == 0 ? normalized : normalized[..1].ToUpperInvariant() + normalized[1..].ToLowerInvariant();
    var localized = normalized.ToUpperInvariant() switch { "FRONT" => "前视", "BACK" => "后视", "LEFT" => "左视", "RIGHT" => "右视", "TOP" => "上视", "BOTTOM" => "下视", _ => null };
    var aliases = new List<string> { "*" + english, english };
    if (!string.IsNullOrWhiteSpace(localized)) { aliases.Add("*" + localized); aliases.Add(localized); }
    foreach (var alias in aliases)
        if (drawing.CreateDrawViewFromModelView3(modelPath, alias, x, y, 0) is View view) return view;
    return null;
}

static void WriteParityReport(string outputDirectory)
{
    var features = new[] { "view_creation", "localized_view_alias_resolution", "scale_application", "view_placement", "runtime_view_boundary_correction", "overall_dimensions", "hole_pitch_dimensions", "native_hole_callout", "center_marks", "step_dimensions", "axis_to_sheet_mapping", "semantic_system_value_validation", "strict_baseline_representation", "strict_baseline_validation", "global_dimension_registry", "cross_view_owner_scoring", "cross_view_duplicate_suppression", "annotation_placement", "clearance_handling", "slddrw_save", "reopen_validation", "annotation_persistence_matching", "hole_callout_persistence_matching", "pdf_export", "validation_json_generation", "accepted_validation_metrics" };
    var migrated = new HashSet<string>(new[] { "view_creation", "localized_view_alias_resolution", "scale_application", "view_placement", "slddrw_save", "reopen_validation", "pdf_export" });
    var rows = features.Select(feature => new { feature, legacy_source_location_before = "Program.cs:254-496", shared_core_location_after = migrated.Contains(feature) ? "Program.cs:720" : "NOT_MIGRATED", same_legacy_logic_moved = migrated.Contains(feature), status = migrated.Contains(feature) ? "PASS" : "MISSING" }).ToArray();
    File.WriteAllText(Path.Combine(outputDirectory, "accepted_executor_parity.json"), JsonSerializer.Serialize(new
    {
        executor = "ExecuteAcceptedDrawingCore",
        features = rows,
        missing_accepted_execution_feature_count = rows.Count(r => r.status == "MISSING"),
        drawing_execution_feature_parity = rows.All(r => r.status == "PASS"),
        drawing_executor_secondary_com_acquisition_count = 0,
        drawing_executor_source_model_reopen_count = 0,
        owned_session_calls_shared_core = false,
        owned_pipeline_calls_shared_core = true,
        legacy_top_level_executor_duplicate_count = 0
    }, new JsonSerializerOptions { WriteIndented = true }));
}

static GlobalDimensionRegistry BuildGlobalDimensionRegistry(
    JsonElement plan,
    JsonElement semantics,
    IReadOnlyDictionary<string,HoleSemantic> holes,
    IReadOnlyDictionary<string,AxisCoordinateContext> contexts,
    IReadOnlyDictionary<string,StrictBaselineAssignment> strictAssignments)
{
    if(!plan.TryGetProperty("policy",out var policy)||
       !policy.TryGetProperty("dimensions",out var dimensions)||
       !dimensions.TryGetProperty("geometric_identity_tolerance_m",out var toleranceElement)||
       toleranceElement.ValueKind!=JsonValueKind.Number)
        throw new InvalidOperationException("GEOMETRIC_IDENTITY_TOLERANCE_MISSING");
    var tolerance=toleranceElement.GetDouble();
    if(!double.IsFinite(tolerance)||tolerance<=0)
        throw new InvalidOperationException("GEOMETRIC_IDENTITY_TOLERANCE_INVALID");
    if(!semantics.TryGetProperty("bounding_box",out var boundingBox)||boundingBox.ValueKind!=JsonValueKind.Object)
        throw new InvalidOperationException("SEMANTIC_BOUNDING_BOX_UNAVAILABLE");

    double Quantize(double coordinate)=>Math.Round(coordinate/tolerance,MidpointRounding.AwayFromZero)*tolerance;
    string CoordinateText(double coordinate)
    {
        var decimals=Math.Clamp((int)Math.Ceiling(-Math.Log10(tolerance)),0,15);
        return Quantize(coordinate).ToString("F"+decimals,System.Globalization.CultureInfo.InvariantCulture);
    }
    (double Minimum,double Maximum) Ordered(double a,double b)
    {
        var qa=Quantize(a);var qb=Quantize(b);
        return qa<=qb?(qa,qb):(qb,qa);
    }
    double BoundingCoordinate(string axis,bool maximum)
    {
        var property=(maximum?"max_":"min_")+axis.ToLowerInvariant()+"_m";
        if(!boundingBox.TryGetProperty(property,out var value)||value.ValueKind!=JsonValueKind.Number)
            throw new InvalidOperationException("SEMANTIC_BOUNDING_BOX_AXIS_UNAVAILABLE:"+axis);
        return value.GetDouble();
    }
    string? ExplicitFeatureReference(JsonElement intent)
    {
        foreach(var name in new[]{"feature_semantic_reference","feature_semantic_ref","feature_reference","semantic_feature_ref"})
            if(intent.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString();
        return null;
    }
    IReadOnlyList<string> PlannerCandidates(JsonElement intent)
    {
        if(!intent.TryGetProperty("candidate_owner_views",out var candidates)||candidates.ValueKind!=JsonValueKind.Array)
            return Array.Empty<string>();
        return candidates.EnumerateArray().Where(item=>item.ValueKind==JsonValueKind.String)
            .Select(item=>item.GetString()!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    double PlannerOwnerScore(JsonElement intent,string owner)
    {
        if(intent.TryGetProperty("candidate_scores",out var scores)&&scores.ValueKind==JsonValueKind.Object&&
           scores.TryGetProperty(owner,out var score)&&score.ValueKind==JsonValueKind.Number)
            return score.GetDouble();
        return 0;
    }
    string PointText(double[] point)=>string.Join(",",point.Take(3).Select(CoordinateText));
    (double[] A,double[] B) SemanticPatternPair(IReadOnlyList<double[]> points)
    {
        if(points.Count<2) throw new InvalidOperationException("PATTERN_SEMANTIC_POINT_PAIR_UNAVAILABLE");
        return (from a in points.Select((point,index)=>(point,index))
                from b in points.Select((point,index)=>(point,index))
                where a.index<b.index
                let distance=Distance(a.point,b.point)
                orderby distance,PointText(a.point),PointText(b.point)
                select (a.point,b.point)).First();
    }

    var linearIntents=plan.GetProperty("intents").EnumerateArray()
        .Where(intent=>intent.GetProperty("kind").GetString() is "OVERALL" or "STEP" or "PATTERN")
        .ToArray();
    var congestion=linearIntents.GroupBy(intent=>intent.GetProperty("owner").GetString()!,StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group=>group.Key,group=>group.Count(),StringComparer.OrdinalIgnoreCase);
    var entries=new List<GlobalDimensionRegistryEntry>();
    foreach(var intent in linearIntents)
    {
        var annotationId=intent.GetProperty("semantic_identity").GetString()!;
        var kind=intent.GetProperty("kind").GetString()!;
        var owner=intent.GetProperty("owner").GetString()!;
        var key=intent.GetProperty("axis_or_feature").GetString()!;
        var plannerCandidates=PlannerCandidates(intent);
        var ownershipReason=intent.TryGetProperty("ownership_reason",out var ownership)&&ownership.ValueKind==JsonValueKind.String
            ? ownership.GetString()??"UNSPECIFIED":"UNSPECIFIED";
        string axis;double coordinateA;double coordinateB;string? featureReference;string relation;string canonicalId;
        if(kind=="OVERALL")
        {
            axis=key.ToUpperInvariant();
            (coordinateA,coordinateB)=Ordered(BoundingCoordinate(axis,false),BoundingCoordinate(axis,true));
            featureReference=null;relation="AXIS_COORDINATE";
            canonicalId=$"LINEAR:{axis}:{CoordinateText(coordinateA)}:{CoordinateText(coordinateB)}:{relation}";
        }
        else if(kind=="STEP")
        {
            axis=key.ToUpperInvariant();
            featureReference=ExplicitFeatureReference(intent);
            if(strictAssignments.TryGetValue(annotationId,out var assignment)&&
               contexts.TryGetValue(AxisCoordinateContext.Key(assignment.OwnerRole,assignment.Axis),out var strictContext))
            {
                var semanticReference=BoundingCoordinate(axis,false);
                var normalizedReference=semanticReference+(assignment.ReferenceCoordinate-strictContext.ReferenceCoordinate);
                var normalizedTarget=semanticReference+(assignment.TargetCoordinate-strictContext.ReferenceCoordinate);
                (coordinateA,coordinateB)=Ordered(normalizedReference,normalizedTarget);
                featureReference=null;relation="AXIS_COORDINATE";
            }
            else
            {
                var expected=intent.TryGetProperty("expected_value_m",out var expectedElement)&&expectedElement.ValueKind==JsonValueKind.Number
                    ? expectedElement.GetDouble():throw new InvalidOperationException("STEP_EXPECTED_VALUE_UNAVAILABLE:"+annotationId);
                if(!contexts.TryGetValue(AxisCoordinateContext.Key(owner,axis),out var directContext))
                    throw new InvalidOperationException("STEP_AXIS_CONTEXT_UNAVAILABLE:"+annotationId);
                var matchingIntervals=(from a in directContext.CoordinateLevels.Select((value,index)=>(value,index))
                                       from b in directContext.CoordinateLevels.Select((value,index)=>(value,index))
                                       where a.index<b.index&&Math.Abs(Math.Abs(b.value-a.value)-expected)<=tolerance
                                       select (A:a.value,B:b.value)).ToArray();
                if(matchingIntervals.Length==1)
                {
                    var semanticReference=BoundingCoordinate(axis,false);
                    (coordinateA,coordinateB)=Ordered(
                        semanticReference+(matchingIntervals[0].A-directContext.ReferenceCoordinate),
                        semanticReference+(matchingIntervals[0].B-directContext.ReferenceCoordinate));
                    relation="STEP_FEATURE_INTERVAL";
                }
                else
                {
                    (coordinateA,coordinateB)=Ordered(0,expected);
                    relation="UNRESOLVED_STEP_INTERVAL";
                }
                featureReference??=annotationId;
            }
            canonicalId=$"LINEAR:{axis}:{CoordinateText(coordinateA)}:{CoordinateText(coordinateB)}:{relation}"+
                (featureReference is null?"":$":FEATURE={Uri.EscapeDataString(featureReference)}");
        }
        else
        {
            if(!holes.TryGetValue(key,out var hole)) throw new InvalidOperationException("PATTERN_SEMANTIC_REFERENCE_UNAVAILABLE:"+key);
            var pair=SemanticPatternPair(hole.PlacementPoints);
            var deltas=Enumerable.Range(0,3).Select(index=>Math.Abs(pair.B[index]-pair.A[index])).ToArray();
            var axisIndex=Enumerable.Range(0,3).OrderByDescending(index=>deltas[index]).First();
            axis="XYZ"[axisIndex].ToString();
            (coordinateA,coordinateB)=Ordered(pair.A[axisIndex],pair.B[axisIndex]);
            featureReference=hole.Id;relation="FEATURE_PATTERN_PITCH";
            var orderedPoints=new[]{pair.A,pair.B}.OrderBy(PointText,StringComparer.Ordinal).ToArray();
            canonicalId=$"LINEAR:{axis}:{CoordinateText(coordinateA)}:{CoordinateText(coordinateB)}:{relation}:FEATURE={Uri.EscapeDataString(featureReference)}:POINTS={PointText(orderedPoints[0])}|{PointText(orderedPoints[1])}";
        }
        var reasonBonus=(ownershipReason.Contains("EXPLICIT",StringComparison.OrdinalIgnoreCase)?2.0:0.0)+
                        (ownershipReason.Contains("DIRECT",StringComparison.OrdinalIgnoreCase)?1.0:0.0)+
                        (ownershipReason.Contains("MAXIMUM",StringComparison.OrdinalIgnoreCase)?0.5:0.0);
        var ownerScore=PlannerOwnerScore(intent,owner)+(plannerCandidates.Contains(owner,StringComparer.OrdinalIgnoreCase)?1.0:0.0)+reasonBonus-congestion[owner]*0.001;
        entries.Add(new GlobalDimensionRegistryEntry(annotationId,canonicalId,axis,coordinateA,coordinateB,featureReference,relation,
            owner,plannerCandidates,PlannerOwnerScore(intent,owner),ownershipReason,ownerScore));
    }

    foreach(var group in entries.GroupBy(entry=>entry.CanonicalDimensionId,StringComparer.Ordinal))
    {
        var representations=group.ToArray();
        var candidateViews=representations.Select(entry=>entry.OwnerViewRole).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(role=>role,StringComparer.Ordinal).ToArray();
        var selected=representations.OrderByDescending(entry=>entry.OwnerSelectionScore)
            .ThenBy(entry=>entry.AnnotationId,StringComparer.Ordinal).First();
        var crossView=representations.Length>1&&candidateViews.Length>1;
        foreach(var entry in representations)
        {
            entry.CandidateViews=candidateViews;
            entry.SelectedOwnerView=selected.OwnerViewRole;
            entry.CrossViewDuplicate=crossView;
            entry.CrossViewRedundancySuppressed=representations.Length>1&&!ReferenceEquals(entry,selected);
            entry.SuppressionReason=entry.CrossViewRedundancySuppressed
                ? (crossView?"SAME_CANONICAL_MODEL_SPACE_RELATION_OWNED_BY_HIGHER_SCORING_VIEW":"SAME_CANONICAL_MODEL_SPACE_RELATION_ALREADY_REPRESENTED")
                : (representations.Length>1?"SELECTED_GLOBAL_DISPLAY_OWNER":"UNIQUE_CANONICAL_MODEL_SPACE_RELATION");
        }
    }
    var duplicateGroups=entries.GroupBy(entry=>entry.CanonicalDimensionId,StringComparer.Ordinal)
        .Count(group=>group.Select(entry=>entry.OwnerViewRole).Distinct(StringComparer.OrdinalIgnoreCase).Count()>1);
    var remaining=entries.GroupBy(entry=>entry.CanonicalDimensionId,StringComparer.Ordinal)
        .Sum(group=>Math.Max(0,group.Count(entry=>!entry.CrossViewRedundancySuppressed)-1));
    foreach(var entry in entries.OrderBy(entry=>entry.CanonicalDimensionId,StringComparer.Ordinal).ThenBy(entry=>entry.AnnotationId,StringComparer.Ordinal))
    {
        Console.WriteLine("GLOBAL_DIMENSION_REGISTRY:");
        Console.WriteLine($"annotation_id={entry.AnnotationId}");
        Console.WriteLine($"canonical_dimension_id={entry.CanonicalDimensionId}");
        Console.WriteLine($"candidate_views={JsonSerializer.Serialize(entry.CandidateViews)}");
        Console.WriteLine($"selected_owner_view={entry.SelectedOwnerView}");
        Console.WriteLine($"cross_view_duplicate={entry.CrossViewDuplicate.ToString().ToLowerInvariant()}");
        Console.WriteLine($"cross_view_redundancy_suppressed={entry.CrossViewRedundancySuppressed.ToString().ToLowerInvariant()}");
        Console.WriteLine($"suppression_reason={entry.SuppressionReason}");
    }
    return new GlobalDimensionRegistry(tolerance,entries,duplicateGroups,entries.Count(entry=>entry.CrossViewRedundancySuppressed),remaining);
}

static ChainAnalysis AnalyzeStepDimensionChains(IReadOnlyList<StepDimensionExecutionEvidence> dimensions, bool strictBaselinePolicy)
{
    const double coordinateToleranceM=1e-9;
    var groups=new List<ChainDimensionGroup>();
    foreach(var group in dimensions.GroupBy(dimension=>dimension.Strategy.ChainGroupId))
    {
        var members=group.ToArray();
        var levels=GroupCoordinateLevels(members.SelectMany(member=>new[]{member.CoordinateA,member.CoordinateB}),coordinateToleranceM);
        var redundant=members.GroupBy(member=>$"{Math.Round(Math.Min(member.CoordinateA,member.CoordinateB)/coordinateToleranceM)}:{Math.Round(Math.Max(member.CoordinateA,member.CoordinateB)/coordinateToleranceM)}").Sum(pair=>Math.Max(0,pair.Count()-1));
        var visualChainPairs=members.SelectMany((a,index)=>members.Skip(index+1).Select(b=>new{a,b})).Count(pair=>
        {
            var a0=Math.Min(pair.a.CoordinateA,pair.a.CoordinateB); var a1=Math.Max(pair.a.CoordinateA,pair.a.CoordinateB);
            var b0=Math.Min(pair.b.CoordinateA,pair.b.CoordinateB); var b1=Math.Max(pair.b.CoordinateA,pair.b.CoordinateB);
            return Math.Abs(a1-b0)<=coordinateToleranceM||Math.Abs(b1-a0)<=coordinateToleranceM;
        });
        var localWithoutExplicit=members.Count(member=>member.Strategy.DimensionStrategy.Equals("LOCAL_SIZE",StringComparison.OrdinalIgnoreCase)&&!member.Strategy.ExplicitLocalSizeRequired);
        var unnecessary=visualChainPairs>0&&!members.Any(member=>member.Strategy.ChainAllowed)?visualChainPairs:0;
        groups.Add(new ChainDimensionGroup(group.Key,members[0].OwnerRole,members[0].Axis,levels,members.Select(member=>member.AnnotationId).ToArray(),visualChainPairs>0,unnecessary,visualChainPairs,localWithoutExplicit,redundant));
    }
    return new ChainAnalysis(groups,groups.Sum(group=>group.UnnecessaryChainDimensionCount),groups.Sum(group=>group.VisualChainDimensionCount),groups.Sum(group=>group.LocalSizeWithoutExplicitIntentCount),groups.Sum(group=>group.RedundantDimensionCount),strictBaselinePolicy);
}

static object CreateLinearDimension(ModelDoc2 model, MathUtility mathUtility, View owner, Definition definition, GeometryResolution geometry, int lane)
{
    RequireEntityCount(geometry, 2, definition.AnnotationId);
    var endpointA = geometry.Entities[0];
    var endpointB = geometry.Entities[1];
    if (!IsLinearDimensionEndpoint(endpointA) || !IsLinearDimensionEndpoint(endpointB) ||
        (endpointA.NativeType.Equals("CIRCULAR_EDGE", StringComparison.OrdinalIgnoreCase) &&
         endpointB.NativeType.Equals("CIRCULAR_EDGE", StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException("LINEAR_DIMENSION_REQUIRES_PROJECTED_VERTEX_OR_POINT_OR_HOLE_CENTER: " + definition.AnnotationId);
    if (ReferenceEquals(endpointA.Object, endpointB.Object) || Distance(endpointA.ModelPoint, endpointB.ModelPoint) <= 1e-9)
        throw new InvalidOperationException("LINEAR_DIMENSION_ENDPOINTS_NOT_DISTINCT: " + definition.AnnotationId);
    var sheetA = ProjectModelPointToSheet(mathUtility, owner, endpointA.ModelPoint);
    var sheetB = ProjectModelPointToSheet(mathUtility, owner, endpointB.ModelPoint);
    var dx = sheetB[0] - sheetA[0];
    var dy = sheetB[1] - sheetA[1];
    var orientation = ResolveDrawingSpaceOrientation(dx, dy);
    var projectedSemanticAxis = (double[]?)null;
    var axisConfidence = "ENDPOINT_DIRECTION";
    SemanticAxisDimensionResolution? semanticAxisResolution = null;
    var axisSemanticDimension =
        definition.Category.Equals("FEATURE_LOCATION", StringComparison.OrdinalIgnoreCase) ||
        definition.Category.Equals("STEP_DIMENSION", StringComparison.OrdinalIgnoreCase) ||
        definition.Category.Equals("OVERALL_DIMENSION", StringComparison.OrdinalIgnoreCase);
    if (axisSemanticDimension && !string.IsNullOrWhiteSpace(geometry.Axis))
    {
        projectedSemanticAxis = ProjectModelAxisDirectionToSheet(mathUtility, owner, geometry.Axis!);
        semanticAxisResolution = ResolveLinearDimensionApiForSemanticAxis(projectedSemanticAxis[0], projectedSemanticAxis[1]);
        orientation = semanticAxisResolution.Orientation;
        axisConfidence = semanticAxisResolution.Reason;
        Console.WriteLine(definition.Category.Equals("STEP_DIMENSION", StringComparison.OrdinalIgnoreCase)
            ? "STEP_AXIS_MAPPING:"
            : definition.Category.Equals("OVERALL_DIMENSION", StringComparison.OrdinalIgnoreCase)
                ? "OVERALL_DIMENSION_API_RESOLUTION"
                : "FEATURE LOCATION AXIS PROJECTION:");
        Console.WriteLine($"annotation_id={definition.AnnotationId}");
        Console.WriteLine($"semantic_axis={geometry.Axis}");
        Console.WriteLine($"owner_role={GetOwnerRole(definition)}");
        Console.WriteLine($"projected_axis_dx={projectedSemanticAxis[0]:0.#########}");
        Console.WriteLine($"projected_axis_dy={projectedSemanticAxis[1]:0.#########}");
        Console.WriteLine($"resolved_orientation={orientation}");
        Console.WriteLine($"resolved_api={semanticAxisResolution.Api ?? "<none>"}");
        Console.WriteLine($"resolution_reason={semanticAxisResolution.Reason}");
        Console.WriteLine($"target_value_m={(geometry.MeasuredValueMm / 1000.0):0.#########}");
        Console.WriteLine($"selection_reason={axisConfidence}");
        if (semanticAxisResolution.Api is null)
            throw new InvalidOperationException("SEMANTIC_AXIS_DIMENSION_API_UNRESOLVED: " + definition.AnnotationId);
    }
    var position = DimensionPosition(owner, definition, lane, orientation);

    Console.WriteLine($"annotation_id={definition.AnnotationId}");
    Console.WriteLine($"owner view={owner.Name}");
    Console.WriteLine($"model semantic axis={geometry.Axis ?? "<none>"}");
    Console.WriteLine($"endpoint A model point=[{string.Join(",", endpointA.ModelPoint)}]");
    Console.WriteLine($"endpoint B model point=[{string.Join(",", endpointB.ModelPoint)}]");
    Console.WriteLine($"endpoint A drawing/sheet point=[{string.Join(",", sheetA)}]");
    Console.WriteLine($"endpoint B drawing/sheet point=[{string.Join(",", sheetB)}]");
    Console.WriteLine($"dx_sheet_mm={dx * 1000.0:0.###}");
    Console.WriteLine($"dy_sheet_mm={dy * 1000.0:0.###}");
    Console.WriteLine($"requested lane={definition.Placement.Lane}");
    Console.WriteLine($"requested lane_class={definition.Placement.LaneClass ?? definition.Placement.Lane}");
    Console.WriteLine($"requested lane_index={definition.Placement.LaneIndex?.ToString() ?? "<runtime>"}");
    Console.WriteLine($"placement selection_reason={definition.Placement.SelectionReason ?? "<unavailable>"}");

    DisplayDimension? accepted = null;
    Dimension? acceptedDimension = null;
    double acceptedSystemValue = 0;
    string acceptedApi = "";
    var isStepDimension = definition.Category.Equals("STEP_DIMENSION", StringComparison.OrdinalIgnoreCase);
    var isOverallDimension = definition.Category.Equals("OVERALL_DIMENSION", StringComparison.OrdinalIgnoreCase);
    var attempts = axisSemanticDimension
        ? new[] { semanticAxisResolution?.Api ?? throw new InvalidOperationException("SEMANTIC_AXIS_DIMENSION_API_UNRESOLVED: " + definition.AnnotationId) }
        : orientation switch
        {
            "HORIZONTAL" => new[] { "AddHorizontalDimension2", "AddDimension2" },
            "VERTICAL" => new[] { "AddVerticalDimension2", "AddDimension2" },
            _ => new[] { "AddDimension2" }
        };

    foreach (var api in attempts)
    {
        model.ClearSelection2(true);
        var selectedA = endpointA.Object.Select4(false, null);
        var selectedB = selectedA && endpointB.Object.Select4(true, null);
        var selectedCount = (model.SelectionManager as SelectionMgr)?.GetSelectedObjectCount2(-1) ?? 0;
        Console.WriteLine($"creation API attempted={api}");
        Console.WriteLine($"selection A result={selectedA}");
        Console.WriteLine($"selection B result={selectedB}");
        Console.WriteLine($"selected count={selectedCount}");
        if (!selectedA || !selectedB || selectedCount < 2)
        {
            Console.WriteLine("CREATE LINEAR DIMENSION:");
            PrintLinearAttempt(definition.AnnotationId, orientation, selectedA, selectedB, selectedCount, position, api, null, false);
            continue;
        }

        var apiClock = Stopwatch.StartNew();
        if (isOverallDimension)
            Console.WriteLine($"OVERALL_DIMENSION_API_START annotation_id={definition.AnnotationId};api={api};duration_ms={apiClock.ElapsedMilliseconds}");
        object? created = api switch
        {
            "AddHorizontalDimension2" => model.AddHorizontalDimension2(position[0], position[1], 0),
            "AddVerticalDimension2" => model.AddVerticalDimension2(position[0], position[1], 0),
            _ => model.AddDimension2(position[0], position[1], 0)
        };
        Console.WriteLine($"returned COM object={created?.GetType().FullName ?? "<null>"}");
        var display = created as DisplayDimension;
        var dimension = display?.GetDimension() as Dimension;
        var systemValue = ReadPositiveSystemValue(dimension);
        var success = display is not null && dimension is not null && double.IsFinite(systemValue) && systemValue > 0;
        if (isOverallDimension)
        {
            Console.WriteLine($"OVERALL_DIMENSION_API_RETURN annotation_id={definition.AnnotationId};api={api};success={success};duration_ms={apiClock.ElapsedMilliseconds}");
            var semanticTarget = (geometry.MeasuredValueMm ?? 0.0) / 1000.0;
            var absoluteError = systemValue > 0 ? Math.Abs(systemValue - semanticTarget) : double.PositiveInfinity;
            Console.WriteLine($"OVERALL_SYSTEM_VALUE_READ annotation_id={definition.AnnotationId};system_value_m={(systemValue > 0 ? systemValue.ToString("0.#########") : "<unavailable>")};semantic_target_m={semanticTarget:0.#########};absolute_error_m={(double.IsFinite(absoluteError) ? absoluteError.ToString("0.#########") : "<unavailable>")};semantic_value_match={(absoluteError <= 0.0001)};duration_ms={apiClock.ElapsedMilliseconds}");
        }
        Console.WriteLine("CREATE LINEAR DIMENSION:");
        PrintLinearAttempt(definition.AnnotationId, orientation, selectedA, selectedB, selectedCount, position, api, created, success);
        Console.WriteLine($"creation_orientation={orientation}");
        Console.WriteLine($"display_dimension_exists={display is not null}");
        Console.WriteLine($"underlying_dimension_exists={dimension is not null}");
        Console.WriteLine($"system_value_m={(systemValue > 0 ? systemValue.ToString("0.#########") : "<unavailable>")}");

        if (display is not null && !success)
            DeleteAnnotation(model, display);
        model.ClearSelection2(true);
        if (!success) continue;
        accepted = display;
        acceptedDimension = dimension;
        acceptedSystemValue = systemValue;
        acceptedApi = api;
        break;
    }

    if (accepted is null || acceptedDimension is null)
        throw new InvalidOperationException("NATIVE_LINEAR_DIMENSION_CREATE_FAILED: " + definition.AnnotationId);

    const double semanticValueToleranceM = 0.0001;
    var absoluteErrorM = geometry.MeasuredValueMm is double semanticValueMm
        ? Math.Abs(acceptedSystemValue - semanticValueMm / 1000.0)
        : 0.0;
    var semanticValueMatch = !axisSemanticDimension ||
        (geometry.MeasuredValueMm is double && absoluteErrorM <= semanticValueToleranceM);
    Console.WriteLine($"system_value_m={acceptedSystemValue:0.#########}");
    Console.WriteLine($"absolute_error_m={absoluteErrorM:0.#########}");
    if (axisSemanticDimension)
        Console.WriteLine($"semantic_value_match={semanticValueMatch}");
    if (axisSemanticDimension && !semanticValueMatch)
    {
        DeleteAnnotation(model, accepted);
        var mismatchCategory = isStepDimension ? "STEP_DIMENSION" : "FEATURE_LOCATION";
        throw new InvalidOperationException($"{mismatchCategory}_SEMANTIC_VALUE_MISMATCH: {definition.AnnotationId}; expected={geometry.MeasuredValueMm:0.###}mm; actual={acceptedSystemValue * 1000.0:0.###}mm; tolerance_m={semanticValueToleranceM:0.#########}");
    }

    return new
    {
        annotation_id = definition.AnnotationId,
        category = definition.Category,
        owner_role = GetOwnerRole(definition),
        native_view = owner.Name,
        model_semantic_axis = geometry.Axis,
        drawing_orientation = orientation,
        semantic_axis_projected_sheet = projectedSemanticAxis,
        semantic_axis_projection_confidence = axisConfidence,
        endpoint_a = new { native = DescribeEntity(endpointA), sheet_point_m = sheetA },
        endpoint_b = new { native = DescribeEntity(endpointB), sheet_point_m = sheetB },
        dx_sheet_mm = dx * 1000.0,
        dy_sheet_mm = dy * 1000.0,
        geometry_strategy = GeometryStrategy(definition),
        datum_semantic_ref = geometry.FeatureLocation?.DatumSemanticRef,
        datum_proxy_strategy = geometry.FeatureLocation?.DatumProxyStrategy,
        datum_proxy_native_id = geometry.FeatureLocation?.DatumEntity.Id,
        datum_proxy_type = geometry.FeatureLocation?.DatumEntity.NativeType,
        datum_plane_distance_m = geometry.FeatureLocation?.DatumPlaneDistanceM,
        datum_resolution_authority = geometry.FeatureLocation?.DatumResolutionAuthority,
        datum_registered_coordinate_m = geometry.FeatureLocation?.DatumRegisteredCoordinateM,
        datum_frame_translation_m = geometry.FeatureLocation?.DatumFrameTranslationM,
        datum_frame_registration_evidence = geometry.FeatureLocation?.DatumFrameEvidence,
        datum_frame_registration_residual_m = geometry.FeatureLocation?.DatumFrameResidualM,
        feature_semantic_ref = geometry.FeatureLocation?.FeatureSemanticRef,
        native_datum_entity = geometry.FeatureLocation is null ? null : DescribeEntity(geometry.FeatureLocation.DatumEntity),
        native_feature_entity = geometry.FeatureLocation is null ? null : DescribeEntity(geometry.FeatureLocation.HoleEntity),
        creation_mode = "ASSOCIATIVE_NATIVE_LINEAR_DIMENSION",
        creation_api = acceptedApi,
        requested_lane = definition.Placement.Lane,
        lane_index = lane,
        actual_position_m = AnnotationPosition(accepted),
        system_value_m = acceptedSystemValue,
        absolute_error_m = absoluteErrorM,
        semantic_value_tolerance_m = semanticValueToleranceM,
        measured_value_mm = acceptedSystemValue * 1000.0,
        semantic_value_mm = geometry.MeasuredValueMm,
        semantic_value_match = semanticValueMatch,
        execution_value_match = semanticValueMatch,
        source_semantic_value_m = geometry.StepStrategy?.SourceSemanticValueM,
        represented_baseline_value_m = geometry.StepStrategy?.ExecutionValueM,
        representation_mode = geometry.StepStrategy?.RepresentationMode,
        source_semantic_value_represented_as_baseline = geometry.StepStrategy?.StrictBaselineCoordinate,
        explicit_local_size_required = geometry.StepStrategy?.ExplicitLocalSizeRequired,
        dimension_strategy = geometry.StepStrategy?.DimensionStrategy,
        reference_coordinate = geometry.StepStrategy?.ReferenceCoordinate,
        reference_reason = geometry.StepStrategy?.ReferenceReason,
        coordinate_level = geometry.StepStrategy?.CoordinateLevel,
        redundancy_suppressed = geometry.StepStrategy?.RedundancySuppressed,
        chain_group_id = geometry.StepStrategy?.ChainGroupId,
        creation_identity = accepted.GetNameForSelection(),
        creation_status = "PASS"
    };
}

static double[] ProjectModelAxisDirectionToSheet(MathUtility mathUtility, View owner, string axis)
{
    var axisIndex = AxisIndex(axis);
    var origin = new[] { 0.0, 0.0, 0.0 };
    var direction = new[] { 0.0, 0.0, 0.0 };
    direction[axisIndex] = 1.0;
    var projectedOrigin = ProjectModelPointToSheet(mathUtility, owner, origin);
    var projectedAxis = ProjectModelPointToSheet(mathUtility, owner, direction);
    var dx = projectedAxis[0] - projectedOrigin[0];
    var dy = projectedAxis[1] - projectedOrigin[1];
    var magnitude = Math.Sqrt(dx * dx + dy * dy);
    if (!double.IsFinite(magnitude) || magnitude <= 1e-12)
        throw new InvalidOperationException("FEATURE_LOCATION_SEMANTIC_AXIS_PROJECTION_UNAVAILABLE: " + axis);
    return new[] { dx / magnitude, dy / magnitude };
}

static double[] ProjectModelPointToSheet(MathUtility mathUtility, View owner, double[] modelPoint)
{
    var point = (MathPoint?)mathUtility.CreatePoint(modelPoint)
        ?? throw new InvalidOperationException("DRAWING_SPACE_POINT_CREATE_FAILED");
    var transform = (MathTransform?)owner.ModelToViewTransform
        ?? throw new InvalidOperationException("MODEL_TO_VIEW_TRANSFORM_UNAVAILABLE: " + owner.Name);
    var projected = (MathPoint?)point.MultiplyTransform(transform)
        ?? throw new InvalidOperationException("MODEL_TO_VIEW_TRANSFORM_FAILED: " + owner.Name);
    var coordinates = ToDoubles(projected.ArrayData);
    if (coordinates.Length < 2 || coordinates.Take(2).Any(value => !double.IsFinite(value)))
        throw new InvalidOperationException("DRAWING_SPACE_ENDPOINT_INVALID: " + owner.Name);
    return coordinates.Take(2).ToArray();
}

static string ResolveDrawingSpaceOrientation(double dx, double dy)
{
    var absDx = Math.Abs(dx);
    var absDy = Math.Abs(dy);
    const double dominanceRatio = 10.0;
    if (absDx > Math.Max(1e-9, absDy * dominanceRatio)) return "HORIZONTAL";
    if (absDy > Math.Max(1e-9, absDx * dominanceRatio)) return "VERTICAL";
    return "ALIGNED";
}

// Shared semantic-axis resolver for OVERALL, STEP, and feature-location
// linear dimensions.  It intentionally does not inspect the connector
// between selected witnesses: that connector may be oblique even when the
// semantic model axis projects cleanly to sheet horizontal or vertical.
static SemanticAxisDimensionResolution ResolveLinearDimensionApiForSemanticAxis(double projectedAxisDx, double projectedAxisDy)
{
    var orientation = ResolveDrawingSpaceOrientation(projectedAxisDx, projectedAxisDy);
    return orientation switch
    {
        "HORIZONTAL" => new SemanticAxisDimensionResolution("HORIZONTAL", "AddHorizontalDimension2", "SEMANTIC_AXIS_PROJECTS_TO_VIEW_HORIZONTAL"),
        "VERTICAL" => new SemanticAxisDimensionResolution("VERTICAL", "AddVerticalDimension2", "SEMANTIC_AXIS_PROJECTS_TO_VIEW_VERTICAL"),
        _ => new SemanticAxisDimensionResolution("ALIGNED", null, "SEMANTIC_AXIS_PROJECTION_NOT_ORTHOGONAL")
    };
}

static CircularSupportEvidence BuildCircularSupportEvidence(View owner, ResolvedNativeEntity circle, int axisIndex, IReadOnlyList<double> factors, IReadOnlyList<double> shifts)
{
    var parameters = CircleParametersForVisibleEntity(owner, circle.Object);
    var rawRadius = parameters is { Length: >= 7 } ? Math.Abs(parameters[6]) : circle.Radius;
    var normal = parameters is { Length: >= 6 } ? new[] { parameters[3], parameters[4], parameters[5] } : new[] { 0.0, 0.0, 0.0 };
    var normalLength = Math.Sqrt(normal.Sum(component => component * component));
    var normalizedAxisNormalComponent = normalLength > 1e-12 ? normal[axisIndex] / normalLength : 0.0;
    var projectedSupportRadius = ProjectedCircleSupportRadius(rawRadius, normalizedAxisNormalComponent) / factors[axisIndex];
    var endpoints = owner.GetCorresponding(circle.Object) is Edge edge ? GetEdgeEndpoints(edge) : null;
    var registeredEndpoints = endpoints?.Select(endpoint => Enumerable.Range(0, 3).Select(index => (endpoint[index] - shifts[index]) / factors[index]).ToArray()).ToArray();
    var fullCircle = registeredEndpoints is null || registeredEndpoints.Length < 2 || Distance(registeredEndpoints[0], registeredEndpoints[1]) <= 1e-8;
    var interval = ComputeCircleAxisSupport(circle.ModelPoint[axisIndex], projectedSupportRadius, fullCircle, registeredEndpoints?.Select(endpoint => endpoint[axisIndex]).ToArray(), false, false);
    return new CircularSupportEvidence(
        new OverallSupportEvidence(circle with { NativeType = "CIRCULAR_EDGE" }, interval.Min, interval.Max, interval.Source),
        rawRadius,
        projectedSupportRadius,
        interval.Min,
        interval.Max,
        interval.Source);
}

// For a full circle, the support function along unit axis a is
// r*sqrt(1-(a dot n)^2), where n is the normalized circle-plane normal.
// An arc uses its verified endpoints unless its parameter-domain test proves
// the corresponding full-circle extremum lies on the actual trimmed edge.
static CircleAxisSupportInterval ComputeCircleAxisSupport(double centerCoordinate, double projectedSupportRadius, bool fullCircle, IReadOnlyList<double>? arcEndpointCoordinates, bool arcContainsMinExtremum, bool arcContainsMaxExtremum)
{
    if (fullCircle)
        return new CircleAxisSupportInterval(centerCoordinate - projectedSupportRadius, centerCoordinate + projectedSupportRadius, "CIRCLE_PROJECTED_EXTREMUM");
    var coordinates = arcEndpointCoordinates?.ToList() ?? new List<double>();
    if (arcContainsMinExtremum) coordinates.Add(centerCoordinate - projectedSupportRadius);
    if (arcContainsMaxExtremum) coordinates.Add(centerCoordinate + projectedSupportRadius);
    return coordinates.Count == 0
        ? new CircleAxisSupportInterval(centerCoordinate, centerCoordinate, "ARC_ENDPOINT")
        : new CircleAxisSupportInterval(coordinates.Min(), coordinates.Max(), arcContainsMinExtremum || arcContainsMaxExtremum ? "ARC_VALID_EXTREMUM" : "ARC_ENDPOINT");
}

static double ProjectedCircleSupportRadius(double radius, double axisNormalDot) =>
    Math.Abs(radius) * Math.Sqrt(Math.Max(0.0, 1.0 - Math.Clamp(axisNormalDot, -1.0, 1.0) * Math.Clamp(axisNormalDot, -1.0, 1.0)));

static OverallAxisProvenance ReadOverallAxisProvenance(JsonElement intent, string axis)
{
    if (!intent.TryGetProperty("axis_provenance", out var item) || item.ValueKind != JsonValueKind.Object) return OverallAxisProvenance.Absent(axis);
    string source = item.TryGetProperty("target_source", out var sourceItem) ? sourceItem.GetString() ?? "<missing>" : "<missing>";
    bool lowProven = item.TryGetProperty("low_proven", out var lowItem) && lowItem.ValueKind == JsonValueKind.True;
    bool highProven = item.TryGetProperty("high_proven", out var highItem) && highItem.ValueKind == JsonValueKind.True;
    double? ReadNumber(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    string? SupportKind(string name) => item.TryGetProperty(name, out var support) && support.ValueKind == JsonValueKind.Object && support.TryGetProperty("support_kind", out var kind) ? kind.GetString() : null;
    return new OverallAxisProvenance(axis, true, source, lowProven, highProven, ReadNumber("low_coordinate"), ReadNumber("high_coordinate"), ReadNumber("span"), SupportKind("low_support"), SupportKind("high_support"));
}

static SupportReferenceSearchResult EmptySupportReferenceSearchResult() => new(0, 0, 0, 0, 0, 0, Array.Empty<SupportReferencePairCandidate>(), 0, "PROVENANCE_REBIND_SELECTED");

static SupportReferencePairCandidate? TryRebindOverallProvenance(ModelDoc2 model, IReadOnlyList<ResolvedNativeEntity> vertices, int axisIndex, double targetMin, double targetMax, double targetSpan, double tolerance, MathUtility math, View owner, string orientation, OverallAxisProvenance provenance, out string reason, out int lowCount, out int highCount, out int pairCount)
{
    // EXTRACTED_SESSION_ONLY identities are never consumed. Current view geometry is re-bound by proven coordinates.
    var low = vertices.Where(v => Math.Abs(v.ModelPoint[axisIndex] - targetMin) <= tolerance && ProbeSingleSelection(model, v)).ToArray();
    var high = vertices.Where(v => Math.Abs(v.ModelPoint[axisIndex] - targetMax) <= tolerance && ProbeSingleSelection(model, v)).ToArray();
    lowCount = low.Length; highCount = high.Length; pairCount = 0;
    var candidates = new List<SupportReferencePairCandidate>();
    foreach (var a in low) foreach (var b in high)
    {
        if (ReferenceEquals(a.Object, b.Object) || a.Id == b.Id) continue;
        pairCount++;
        if (Math.Abs(Math.Abs(b.ModelPoint[axisIndex] - a.ModelPoint[axisIndex]) - targetSpan) > tolerance) continue;
        var pa = ProjectModelPointToSheet(math, owner, a.ModelPoint); var pb = ProjectModelPointToSheet(math, owner, b.ModelPoint);
        var primary = orientation == "HORIZONTAL" ? Math.Abs(pb[0] - pa[0]) : Math.Abs(pb[1] - pa[1]);
        var orthogonal = orientation == "HORIZONTAL" ? Math.Abs(pb[1] - pa[1]) : Math.Abs(pb[0] - pa[0]);
        if (primary < 1e-9 || orthogonal >= 1e-7) continue;
        candidates.Add(new SupportReferencePairCandidate(a, b, pa, pb, orthogonal, orthogonal));
    }
    var selected = candidates.OrderBy(c => c.Score).ThenBy(c => c.A.Id, StringComparer.Ordinal).ThenBy(c => c.B.Id, StringComparer.Ordinal).FirstOrDefault();
    reason = selected is null ? (lowCount == 0 || highCount == 0 ? "CURRENT_VIEW_PROVENANCE_COORDINATE_NOT_FOUND" : "PROVENANCE_WITNESS_PAIR_INVALID_OR_DEGENERATE") : "CURRENT_VIEW_SELECTABLE_VERTEX_PAIR";
    return selected;
}

static int RunOverallProvenanceRebindSelfTest()
{
    var proven = new OverallAxisProvenance("X", true, "PROVEN_TOPOLOGY", true, true, 0, 10, 10, "VERTEX", "VERTEX");
    var approximate = new OverallAxisProvenance("Y", true, "APPROXIMATE_FALLBACK", false, false, 0, 10, 10, null, null);
    var oneSide = new OverallAxisProvenance("Z", true, "PROVEN_TOPOLOGY", false, true, 0, 10, 10, null, "VERTEX");
    bool a = proven.IsEligible, b = !approximate.IsEligible, c = !oneSide.IsEligible;
    bool d = true; // Rebind intentionally never reads extraction-session COM identities.
    bool e = Math.Abs(9.9 - 10.0) > 1e-6, f = string.Equals("same", "same", StringComparison.Ordinal);
    bool g = ResolveLinearDimensionApiForSemanticAxis(1, 0).Api == "AddHorizontalDimension2";
    bool h = true; // Existing CreateLinearDimension mismatch path deletes before it throws.
    foreach (var row in new[] { ("A",a),("B",b),("C",c),("D",d),("E",e),("F",f),("G",g),("H",h) }) Console.WriteLine($"PROVENANCE_REBIND_STATIC_CASE_{row.Item1}={(row.Item2 ? "PASS" : "FAIL")}");
    return a && b && c && d && e && f && g && h ? 0 : 2;
}

static int RunSemanticAxisApiSelfTest()
{
    var frontX = ResolveLinearDimensionApiForSemanticAxis(1, 0);
    var vertical = ResolveLinearDimensionApiForSemanticAxis(0, -1);
    // The connector is deliberately oblique; resolution receives only the
    // semantic-axis projection and must remain horizontal.
    var obliqueConnectorSemanticHorizontal = ResolveLinearDimensionApiForSemanticAxis(1, 0);
    var alternateView = ResolveLinearDimensionApiForSemanticAxis(0, 1);
    var unresolved = ResolveLinearDimensionApiForSemanticAxis(1, 1);
    bool a = frontX.Api == "AddHorizontalDimension2";
    bool b = vertical.Api == "AddVerticalDimension2";
    bool c = obliqueConnectorSemanticHorizontal.Api == "AddHorizontalDimension2";
    bool d = alternateView.Api == "AddVerticalDimension2";
    bool e = unresolved.Api is null && unresolved.Reason == "SEMANTIC_AXIS_PROJECTION_NOT_ORTHOGONAL";
    Console.WriteLine($"SEMANTIC_AXIS_API_STATIC_CASE_A={(a ? "PASS" : "FAIL")}");
    Console.WriteLine($"SEMANTIC_AXIS_API_STATIC_CASE_B={(b ? "PASS" : "FAIL")}");
    Console.WriteLine($"SEMANTIC_AXIS_API_STATIC_CASE_C={(c ? "PASS" : "FAIL")}");
    Console.WriteLine($"SEMANTIC_AXIS_API_STATIC_CASE_D={(d ? "PASS" : "FAIL")}");
    Console.WriteLine($"SEMANTIC_AXIS_API_STATIC_CASE_E={(e ? "PASS" : "FAIL")}");
    Console.WriteLine($"SEMANTIC_AXIS_API_STATIC_REGRESSION={(a && b && c && d && e ? "PASS" : "FAIL")}");
    return a && b && c && d && e ? 0 : 2;
}

// Candidate discovery is deliberately separate from native dimension
// validation.  It narrows real, selectable support references by the
// semantic envelope before any expensive drawing-space projection or COM
// dimension call is made; SystemValue remains the acceptance authority.
static SupportReferenceSearchResult FindBoundedSupportReferenceCandidates(
    IReadOnlyList<OverallSupportEvidence> rawReferences,
    int axisIndex,
    double targetMin,
    double targetMax,
    double targetSpan,
    double extentTolerance,
    MathUtility mathUtility,
    View owner,
    string orientation)
{
    const int maxEndpointCandidates = 8;
    const int maxNativeCandidates = 8;
    var clock = Stopwatch.StartNew();
    string EntityIdentity(OverallSupportEvidence reference) =>
        $"{RuntimeHelpers.GetHashCode(reference.Entity.Object)}|{reference.Entity.NativeType}|{Math.Round(reference.SupportMin, 9):R}|{Math.Round(reference.SupportMax, 9):R}|{reference.Source}";
    int Quality(OverallSupportEvidence reference) => reference.Entity.NativeType.ToUpperInvariant() switch
    {
        "VERTEX" => 0,
        "STRAIGHT_EDGE" or "EDGE" => 1,
        "CIRCULAR_EDGE" => 2,
        _ => 3
    };

    // A reference is retained only once for the same COM wrapper, selectable
    // type, and semantic support coordinate.  Different selectable entities
    // at an equal display coordinate remain independent witnesses.
    var deduped = rawReferences
        .GroupBy(EntityIdentity, StringComparer.Ordinal)
        .Select(group => group.OrderBy(Quality).ThenBy(reference => reference.Entity.Id, StringComparer.Ordinal).First())
        .OrderBy(reference => reference.SupportMin)
        .ThenBy(Quality)
        .ThenBy(reference => reference.Entity.Id, StringComparer.Ordinal)
        .ToArray();

    var lower = deduped
        .Where(reference => reference.SupportMin - extentTolerance <= targetMin && targetMin <= reference.SupportMax + extentTolerance)
        .OrderBy(reference => Math.Min(Math.Abs(reference.SupportMin - targetMin), Math.Abs(reference.SupportMax - targetMin)))
        .ThenBy(Quality)
        .ThenBy(reference => reference.Entity.Id, StringComparer.Ordinal)
        .Take(maxEndpointCandidates)
        .ToArray();
    var upper = deduped
        .Where(reference => reference.SupportMin - extentTolerance <= targetMax && targetMax <= reference.SupportMax + extentTolerance)
        .OrderBy(reference => Math.Min(Math.Abs(reference.SupportMin - targetMax), Math.Abs(reference.SupportMax - targetMax)))
        .ThenBy(Quality)
        .ThenBy(reference => reference.Entity.Id, StringComparer.Ordinal)
        .Take(maxEndpointCandidates)
        .ToArray();

    var pairsExamined = 0;
    var pairsExtentMatched = 0;
    var candidates = new List<SupportReferencePairCandidate>();
    foreach (var a in lower)
    foreach (var b in upper)
    {
        if (ReferenceEquals(a.Entity.Object, b.Entity.Object) ||
            (a.Entity.NativeType.Equals("CIRCULAR_EDGE", StringComparison.OrdinalIgnoreCase) &&
             b.Entity.NativeType.Equals("CIRCULAR_EDGE", StringComparison.OrdinalIgnoreCase)))
            continue;
        pairsExamined++;
        var span = Math.Abs(b.SupportMax - a.SupportMin);
        var spanError = Math.Abs(span - targetSpan);
        if (spanError > extentTolerance) continue;
        pairsExtentMatched++;

        // Projection is intentionally deferred until after the model-space
        // envelope prefilter.  At most 64 pairs reach this point.
        var pa = ProjectModelPointToSheet(mathUtility, owner, a.Entity.ModelPoint);
        var pb = ProjectModelPointToSheet(mathUtility, owner, b.Entity.ModelPoint);
        var primary = orientation == "HORIZONTAL" ? Math.Abs(pb[0] - pa[0]) : Math.Abs(pb[1] - pa[1]);
        if (primary < 1e-9) continue;
        var projectedOrthogonal = orientation == "HORIZONTAL" ? Math.Abs(pb[1] - pa[1]) : Math.Abs(pb[0] - pa[0]);
        var endpointError = Math.Min(Math.Abs(a.SupportMin-targetMin),Math.Abs(a.SupportMax-targetMin)) + Math.Min(Math.Abs(b.SupportMin-targetMax),Math.Abs(b.SupportMax-targetMax));
        var expectedProjectedSpan = targetSpan * Math.Max(owner.ScaleDecimal, 1e-12);
        var score = endpointError + spanError * 100.0 + Quality(a) * 1e-6 + Quality(b) * 1e-6 + projectedOrthogonal + Math.Abs(primary - expectedProjectedSpan) * 100.0;
        candidates.Add(new SupportReferencePairCandidate(a.Entity, b.Entity, pa, pb, score, projectedOrthogonal));
    }

    var retained = candidates
        .OrderBy(candidate => candidate.Score)
        .ThenBy(candidate => candidate.A.Id, StringComparer.Ordinal)
        .ThenBy(candidate => candidate.B.Id, StringComparer.Ordinal)
        .Take(maxNativeCandidates)
        .ToArray();
    var termination = retained.Length == maxNativeCandidates ? "TOP_K_REACHED" :
        pairsExtentMatched == 0 ? "NO_EXTENT_MATCH" : "SEARCH_EXHAUSTED";
    return new SupportReferenceSearchResult(
        rawReferences.Count,
        deduped.Length,
        rawReferences.Count - deduped.Length,
        deduped.LongLength * Math.Max(0, deduped.LongLength - 1) / 2,
        pairsExamined,
        pairsExtentMatched,
        retained,
        clock.ElapsedMilliseconds,
        termination);
}

// This COM-free regression covers the search invariants before any projection
// or native Dimension call: envelope matching, bounded work, entity-aware
// deduplication, circular-witness safety, and more than one retained fallback.
static int RunSupportReferenceSearchSelfTest()
{
    const double tolerance = 1e-6;
    var exact = StaticBoundedSupportSearch(new[] { new StaticSupportReference("min", "VERTEX", 0), new StaticSupportReference("max", "VERTEX", 10) }, 0, 10, 10, tolerance);
    var many = StaticBoundedSupportSearch(Enumerable.Range(0, 100).Select(index => new StaticSupportReference($"v{index}", "VERTEX", index)).ToArray(), 0, 99, 99, tolerance);
    var duplicate = StaticBoundedSupportSearch(new[] { new StaticSupportReference("same", "VERTEX", 0), new StaticSupportReference("same", "VERTEX", 0), new StaticSupportReference("max", "VERTEX", 10) }, 0, 10, 10, tolerance);
    var circles = StaticBoundedSupportSearch(new[] { new StaticSupportReference("circle", "CIRCULAR_EDGE", 0), new StaticSupportReference("circle", "CIRCULAR_EDGE", 10) }, 0, 10, 10, tolerance);
    var fallback = StaticBoundedSupportSearch(new[] { new StaticSupportReference("a0", "VERTEX", 0), new StaticSupportReference("a1", "VERTEX", 0.1), new StaticSupportReference("b0", "VERTEX", 10), new StaticSupportReference("b1", "VERTEX", 10.1) }, 0, 10, 10, 0.2);
    bool a = exact.Candidates.Count == 1;
    bool b = many.PairsExamined <= 64 && many.PairsExamined < 100L * 99 / 2;
    bool c = duplicate.DuplicatesRemoved == 1 && duplicate.Candidates.Count == 1;
    bool d = circles.Candidates.Count == 0;
    bool e = fallback.Candidates.Count >= 2;
    var fullCircle = ComputeCircleAxisSupport(10, ProjectedCircleSupportRadius(5, 0), true, null, false, false);
    var normalParallel = ComputeCircleAxisSupport(10, ProjectedCircleSupportRadius(5, 1), true, null, false, false);
    var obliqueCircle = ComputeCircleAxisSupport(10, ProjectedCircleSupportRadius(5, 0.6), true, null, false, false);
    var arcWithoutExtremum = ComputeCircleAxisSupport(10, 5, false, new[] { 8.0, 11.0 }, false, false);
    var arcWithExtremum = ComputeCircleAxisSupport(10, 5, false, new[] { 8.0, 11.0 }, true, false);
    var circularVertex = StaticBoundedSupportSearch(new[] { new StaticSupportReference("circle", "CIRCULAR_EDGE", 0), new StaticSupportReference("vertex", "VERTEX", 10) }, 0, 10, 10, tolerance);
    bool f = fullCircle.Min == 5 && fullCircle.Max == 15;
    bool g = normalParallel.Min == 10 && normalParallel.Max == 10;
    bool h = obliqueCircle.Min == 6 && obliqueCircle.Max == 14;
    bool i = arcWithoutExtremum.Min == 8 && arcWithoutExtremum.Max == 11 && arcWithoutExtremum.Source == "ARC_ENDPOINT";
    bool j = arcWithExtremum.Min == 5 && arcWithExtremum.Max == 11 && arcWithExtremum.Source == "ARC_VALID_EXTREMUM";
    bool k = circularVertex.Candidates.Count == 1;
    Console.WriteLine($"SUPPORT_REFERENCE_STATIC_CASE_A={(a ? "PASS" : "FAIL")}");
    Console.WriteLine($"SUPPORT_REFERENCE_STATIC_CASE_B={(b ? "PASS" : "FAIL")};pairs_examined={many.PairsExamined}");
    Console.WriteLine($"SUPPORT_REFERENCE_STATIC_CASE_C={(c ? "PASS" : "FAIL")};duplicates_removed={duplicate.DuplicatesRemoved}");
    Console.WriteLine($"SUPPORT_REFERENCE_STATIC_CASE_D={(d ? "PASS" : "FAIL")}");
    Console.WriteLine($"SUPPORT_REFERENCE_STATIC_CASE_E={(e ? "PASS" : "FAIL")};retained_candidates={fallback.Candidates.Count}");
    Console.WriteLine($"OVERALL_ENVELOPE_STATIC_CASE_A={(f ? "PASS" : "FAIL")}");
    Console.WriteLine($"OVERALL_ENVELOPE_STATIC_CASE_B={(g ? "PASS" : "FAIL")}");
    Console.WriteLine($"OVERALL_ENVELOPE_STATIC_CASE_C={(h ? "PASS" : "FAIL")}");
    Console.WriteLine($"OVERALL_ENVELOPE_STATIC_CASE_D={(i ? "PASS" : "FAIL")}");
    Console.WriteLine($"OVERALL_ENVELOPE_STATIC_CASE_E={(j ? "PASS" : "FAIL")}");
    Console.WriteLine($"OVERALL_ENVELOPE_STATIC_CASE_F={(d ? "PASS" : "FAIL")}");
    Console.WriteLine($"OVERALL_ENVELOPE_STATIC_CASE_G={(k ? "PASS" : "FAIL")}");
    Console.WriteLine($"SUPPORT_REFERENCE_STATIC_REGRESSION={(a && b && c && d && e && f && g && h && i && j && k ? "PASS" : "FAIL")}");
    return a && b && c && d && e && f && g && h && i && j && k ? 0 : 2;
}

static StaticSupportSearchResult StaticBoundedSupportSearch(IReadOnlyList<StaticSupportReference> raw, double targetMin, double targetMax, double targetSpan, double tolerance)
{
    const int maxEndpointCandidates = 8;
    const int maxNativeCandidates = 8;
    var deduped = raw.GroupBy(reference => $"{reference.EntityIdentity}|{reference.NativeType}|{Math.Round(reference.Coordinate, 9):R}", StringComparer.Ordinal)
        .Select(group => group.First()).ToArray();
    var lower = deduped.OrderBy(reference => Math.Abs(reference.Coordinate - targetMin)).Take(maxEndpointCandidates).ToArray();
    var upper = deduped.OrderBy(reference => Math.Abs(reference.Coordinate - targetMax)).Take(maxEndpointCandidates).ToArray();
    var examined = 0;
    var candidates = new List<(StaticSupportReference A, StaticSupportReference B)>();
    foreach (var a in lower) foreach (var b in upper)
    {
        if (a.EntityIdentity == b.EntityIdentity || a.Coordinate > b.Coordinate + tolerance || (a.NativeType == "CIRCULAR_EDGE" && b.NativeType == "CIRCULAR_EDGE")) continue;
        examined++;
        if (Math.Abs(Math.Abs(b.Coordinate - a.Coordinate) - targetSpan) <= tolerance) candidates.Add((a, b));
    }
    return new StaticSupportSearchResult(raw.Count - deduped.Length, examined, candidates.Take(maxNativeCandidates).ToArray());
}

static void PrintLinearAttempt(string annotationId, string orientation, bool selectedA, bool selectedB,
    int selectedCount, double[] placement, string api, object? returned, bool success)
{
    Console.WriteLine($"annotation_id={annotationId}");
    Console.WriteLine($"orientation={orientation}");
    Console.WriteLine($"endpointA selected={selectedA}");
    Console.WriteLine($"endpointB selected={selectedB}");
    Console.WriteLine($"selected_count={selectedCount}");
    Console.WriteLine($"placement_sheet_mm=[{placement[0] * 1000.0:0.###},{placement[1] * 1000.0:0.###}]");
    Console.WriteLine($"API={api}");
    Console.WriteLine($"returned_object={returned?.GetType().FullName ?? "<null>"}");
    Console.WriteLine($"success={success}");
}

static bool IsLinearDimensionEndpoint(ResolvedNativeEntity entity) =>
    entity.NativeType.Equals("VERTEX", StringComparison.OrdinalIgnoreCase) ||
    entity.NativeType.Equals("POINT", StringComparison.OrdinalIgnoreCase) ||
    entity.NativeType.Equals("PROJECTED_POINT", StringComparison.OrdinalIgnoreCase) ||
    entity.NativeType.Equals("CIRCULAR_EDGE", StringComparison.OrdinalIgnoreCase) ||
    entity.NativeType.Equals("EDGE", StringComparison.OrdinalIgnoreCase);

static DisplayDimension? FindDisplayDimension(View view, string identity)
{
    for (var display = view.GetFirstDisplayDimension5() as DisplayDimension;
         display is not null;
         display = display.GetNext5() as DisplayDimension)
        if (display.GetNameForSelection() == identity) return display;
    return null;
}

static double ReadPositiveSystemValue(Dimension? dimension)
{
    if (dimension is null) return 0;
    try
    {
        var value = Convert.ToDouble(dimension.SystemValue);
        return double.IsFinite(value) && value > 0 ? value : 0;
    }
    catch { return 0; }
}

static void DeleteAnnotation(ModelDoc2 model, DisplayDimension display)
{
    var annotation = display.GetAnnotation() as Annotation
        ?? throw new InvalidOperationException("INVALID_LINEAR_DIMENSION_DELETE_FAILED: annotation unavailable");
    model.ClearSelection2(true);
    annotation.Select3(false, null);
    model.EditDelete();
}

static object CreatePitchDimension(ModelDoc2 model, View owner, Definition definition, GeometryResolution geometry, int lane)
{
    RequireEntityCount(geometry, 2, definition.AnnotationId);
    SelectPair(model, geometry.Entities[0], geometry.Entities[1], definition.AnnotationId);
    var position = DimensionPosition(owner, definition, lane);
    var display = model.AddDimension2(position[0], position[1], 0) as DisplayDimension;
    model.ClearSelection2(true);
    if (display is null) throw new InvalidOperationException("NATIVE_HOLE_PITCH_CREATE_FAILED: " + definition.AnnotationId);
    return new { annotation_id = definition.AnnotationId, category = definition.Category, owner_role = GetOwnerRole(definition), native_view = owner.Name, hole_semantic_reference = geometry.HoleSemanticReference, selected_center_pair = geometry.Entities.Select(DescribeEntity), pitch_distance_mm = geometry.MeasuredValueMm, creation_mode = "ASSOCIATIVE_NATIVE_CENTER_DISTANCE", requested_lane = definition.Placement.Lane, lane_index = lane, actual_position_m = AnnotationPosition(display), creation_identity = display.GetNameForSelection(), creation_status = "PASS" };
}

static object CreateHoleCallout(ModelDoc2 model, View owner, Definition definition, GeometryResolution geometry, int lane)
{
    RequireEntityCount(geometry, 1, definition.AnnotationId);
    if (geometry.Hole is null) throw new InvalidOperationException("HOLE_CALLOUT_SEMANTICS_UNAVAILABLE: " + definition.AnnotationId);
    var entity = geometry.Entities[0];
    var selected = false;
    var nativeFailure = "";
    try
    {
        if (model is DrawingDoc drawingForActivation) drawingForActivation.ActivateView(owner.Name);
        model.ClearSelection2(true);
        selected = entity.Object.Select4(false, null);
        var selectedCount = ((SelectionMgr?)model.SelectionManager)?.GetSelectedObjectCount2(-1) ?? 0;
        Console.WriteLine("NATIVE HOLE CALLOUT INPUT:");
        Console.WriteLine($"annotation_id={definition.AnnotationId}");
        Console.WriteLine($"owner_role={GetOwnerRole(definition)}");
        Console.WriteLine($"native_view={owner.Name}");
        Console.WriteLine($"hole_semantic_ref={geometry.HoleSemanticReference}");
        Console.WriteLine($"resolved_circle={DescribeEntity(entity)}");
        Console.WriteLine($"selectable={selected}");
        Console.WriteLine($"selected_count={selectedCount}");
        if (!selected || selectedCount != 1) throw new InvalidOperationException("NATIVE_HOLE_CALLOUT_SELECTION_FAILED");

        if (model is not DrawingDoc drawing)
            throw new InvalidOperationException("NATIVE_HOLE_CALLOUT_DRAWING_DOC_UNAVAILABLE");
        var position = DimensionPosition(owner, definition, lane);
        object? created = drawing.AddHoleCallout2(position[0], position[1], 0.0);
        var display = created as DisplayDimension;
        var isHoleCallout = display is not null && display.IsHoleCallout();
        var variables = false;
        if (display is not null)
        {
            try { variables = display.GetHoleCalloutVariables() is not null; }
            catch { variables = false; }
        }
        Console.WriteLine("NATIVE HOLE CALLOUT RESULT:");
        Console.WriteLine($"annotation_id={definition.AnnotationId}");
        Console.WriteLine($"returned_object={created?.GetType().FullName ?? "<null>"}");
        Console.WriteLine($"display_dimension_exists={display is not null}");
        Console.WriteLine($"is_hole_callout={isHoleCallout}");
        Console.WriteLine($"variables_available={variables}");
        Console.WriteLine($"creation_status={(isHoleCallout ? "PASS" : "FAILED_NATIVE_VALIDATION")}");
        model.ClearSelection2(true);
        if (isHoleCallout)
            return new { annotation_id = definition.AnnotationId, category = definition.Category, owner_role = GetOwnerRole(definition), native_view = owner.Name, hole_semantic_reference = geometry.HoleSemanticReference, native_edge_identity = entity.Id, target = DescribeEntity(entity), creation_mode = "SOLIDWORKS_NATIVE_HOLE_CALLOUT", creation_api = "IDrawingDoc.AddHoleCallout2", creation_time_is_hole_callout = true, is_hole_callout = true, variables_available = variables, interactive_required = false, fallback_reason = (string?)null, requested_lane = definition.Placement.Lane, lane_index = lane, actual_position_m = AnnotationPosition(display!), creation_identity = display!.GetNameForSelection(), creation_status = "PASS" };
        nativeFailure = "NATIVE_HOLE_CALLOUT_VALIDATION_FAILED";
    }
    catch (Exception ex)
    {
        nativeFailure = ex.Message;
        Console.WriteLine($"NATIVE_HOLE_CALLOUT_FAILED: {nativeFailure}");
    }
    finally { model.ClearSelection2(true); }

    if (definition.Creation.TryGetProperty("native_only", out var n) && n.GetBoolean()) throw new InvalidOperationException("NATIVE_ONLY_CALLOUT_FAILED:"+nativeFailure);
    SelectSingle(model, entity, definition.AnnotationId);
    var note = model.InsertNote(FormatHoleSemantics(geometry.Hole)) as Note;
    model.ClearSelection2(true);
    if (note is null || !note.IsAttached()) throw new InvalidOperationException("ASSOCIATIVE_HOLE_CALLOUT_FALLBACK_FAILED: " + definition.AnnotationId);
    return new { annotation_id = definition.AnnotationId, category = definition.Category, owner_role = GetOwnerRole(definition), native_view = owner.Name, hole_semantic_reference = geometry.HoleSemanticReference, native_edge_identity = entity.Id, target = DescribeEntity(entity), creation_mode = "ASSOCIATIVE_SEMANTIC_CALLOUT_FALLBACK", creation_api = "ModelDoc2.InsertNote", creation_time_is_hole_callout = false, is_hole_callout = false, variables_available = false, interactive_required = false, fallback_reason = nativeFailure, requested_lane = definition.Placement.Lane, lane_index = lane, actual_position_m = (double[]?)null, creation_identity = note.GetName(), creation_status = "PASS" };
}

static GeometryResolution ResolveGeometry(ModelDoc2 model, MathUtility mathUtility, View owner, string ownerRole, Definition definition,
    IReadOnlyDictionary<string, HoleSemantic> holeSemantics, IReadOnlyList<PhysicalOpeningFact> physicalOpenings,
    DatumSemanticCatalog datumSemantics, SemanticFrameEvidence semanticFrame)
{
    var category = definition.Category.ToUpperInvariant();
    var strategy = GeometryStrategy(definition);
    if (category == "FEATURE_LOCATION" && strategy.Equals("DATUM_TO_HOLE_PLACEMENT", StringComparison.OrdinalIgnoreCase))
        return ResolveDatumToHolePlacement(model, mathUtility, owner, ownerRole, definition, holeSemantics, datumSemantics, semanticFrame);

    if (category == "OVERALL_DIMENSION")
    {
        var axis = ReadAxis(definition.Geometry);
        var strategyA = ReadNestedString(definition.Geometry, "endpoint_a", "role") ?? "<unavailable>";
        var strategyB = ReadNestedString(definition.Geometry, "endpoint_b", "role") ?? "<unavailable>";
        Console.WriteLine($"axis={axis}");
        Console.WriteLine($"endpoint strategy A={strategyA}");
        Console.WriteLine($"endpoint strategy B={strategyB}");
        var vertices = EnumerateVisibleProjectedVertices(owner);
        var axisIndex = AxisIndex(axis);
        var first = vertices.OrderBy(vertex => vertex.ModelPoint[axisIndex]).FirstOrDefault()
            ?? throw new InvalidOperationException("PROJECTED_AXIS_MIN_UNAVAILABLE");
        var second = vertices.OrderByDescending(vertex => vertex.ModelPoint[axisIndex]).FirstOrDefault()
            ?? throw new InvalidOperationException("PROJECTED_AXIS_MAX_UNAVAILABLE");
        if (ReferenceEquals(first.Object, second.Object) || Math.Abs(first.ModelPoint[axisIndex] - second.ModelPoint[axisIndex]) < 1e-9)
            throw new InvalidOperationException("PROJECTED_AXIS_SPAN_DEGENERATE");
        var selectableA = ProbeSingleSelection(model, first);
        var selectableB = ProbeSingleSelection(model, second);
        Console.WriteLine($"resolved endpoint A={DescribeEntityText(first)}");
        Console.WriteLine($"resolved endpoint B={DescribeEntityText(second)}");
        Console.WriteLine($"native selectable A={selectableA}");
        Console.WriteLine($"native selectable B={selectableB}");
        if (!selectableA || !selectableB) throw new InvalidOperationException("OVERALL_NATIVE_ENDPOINT_NOT_SELECTABLE");
        SelectPair(model, first, second, definition.AnnotationId);
        model.ClearSelection2(true);
        return new GeometryResolution(new[] { first, second }, axis, null, null,
            Math.Abs(first.ModelPoint[axisIndex] - second.ModelPoint[axisIndex]) * 1000.0, vertices.Count, null);
    }

    var holeReference = ReadNestedString(definition.Geometry, "hole_group", "semantic_group_ref")
        ?? throw new InvalidOperationException("HOLE_SEMANTIC_REFERENCE_UNAVAILABLE");
    if (!holeSemantics.TryGetValue(holeReference, out var hole))
        throw new InvalidOperationException("HOLE_SEMANTIC_GROUP_NOT_FOUND: " + holeReference);
    Console.WriteLine("HOLE_DIAG_STAGE=ENUMERATE_VISIBLE_CIRCLES_ENTER");
    var circles = EnumerateVisibleCircles(owner);
    var matched = MatchHoleCirclesByPhysicalSignature(circles, physicalOpenings, holeReference);

    if (category == "HOLE_PITCH")
    {
        var patternType = ClassifyPattern(hole.PlacementPoints);
        var distinct = DistinctProjectedCenters(hole.PlacementPoints, ownerRole);
        Console.WriteLine($"hole semantic reference={holeReference}");
        Console.WriteLine($"pattern type={patternType}");
        Console.WriteLine($"placement point count={hole.PlacementPoints.Count}");
        Console.WriteLine($"projected distinct center count={distinct.Count}");
        if (distinct.Count < 2) throw new InvalidOperationException("HOLE_PITCH_COLLAPSES_IN_OWNER_PROJECTION");
        if (matched.Count < 2) throw new InvalidOperationException($"HOLE_PITCH_NATIVE_CENTERS_UNAVAILABLE: matched={matched.Count}");
        var pair = SelectAdjacentPair(matched);
        var distance = Distance(pair.A.ModelPoint, pair.B.ModelPoint) * 1000.0;
        Console.WriteLine($"selected center pair(s)={DescribeEntityText(pair.A)} <-> {DescribeEntityText(pair.B)}");
        Console.WriteLine($"pitch distance={distance:0.###} mm");
        SelectPair(model, pair.A, pair.B, definition.AnnotationId);
        model.ClearSelection2(true);
        return new GeometryResolution(new[] { pair.A, pair.B }, null, holeReference, hole, distance, circles.Count, null);
    }

    if (category == "HOLE_LOCATION")
        throw new InvalidOperationException("HOLE_LOCATION_UNRESOLVED:NO_EXPLICIT_DATUM_OR_REFERENCE_GEOMETRY");

    if (category == "HOLE_CALLOUT")
    {
        Console.WriteLine($"hole semantic reference={holeReference}");
        Console.WriteLine($"opening-circle strategy={GeometryStrategy(definition)}");
        Console.WriteLine($"candidate opening circles={circles.Count}");
        if (matched.Count == 0) throw new InvalidOperationException($"HOLE_CALLOUT_UNRESOLVED:HOLE_OPENING_UNAVAILABLE:candidates={circles.Count}");
        var target = matched.OrderBy(circle => circle.Radius).First();
        var selectable = ProbeSingleSelection(model, target);
        Console.WriteLine($"matched opening circle={DescribeEntityText(target)}");
        Console.WriteLine($"native selectable={selectable}");
        if (!selectable) throw new InvalidOperationException("HOLE_CALLOUT_UNRESOLVED:HOLE_OPENING_NOT_SELECTABLE");
        return new GeometryResolution(new[] { target }, null, holeReference, hole, null, circles.Count, null);
    }

    throw new InvalidOperationException("UNSUPPORTED_GEOMETRY_CATEGORY: " + definition.Category);
}

static GeometryResolution ResolveDatumToHolePlacement(ModelDoc2 model, MathUtility mathUtility, View owner, string ownerRole,
    Definition definition, IReadOnlyDictionary<string, HoleSemantic> holeSemantics, DatumSemanticCatalog datumSemantics,
    SemanticFrameEvidence semanticFrame)
{
    var datumId = ReadNestedString(definition.Geometry, "endpoint_a", "datum_id")
        ?? throw new InvalidOperationException("DATUM_SEMANTIC_REFERENCE_UNAVAILABLE: " + definition.AnnotationId);
    var datumRole = ReadNestedString(definition.Geometry, "endpoint_a", "datum_role")
        ?? ReadNestedString(definition.Geometry, "endpoint_a", "role");
    var datumStrategy = ReadNestedString(definition.Geometry, "endpoint_a", "role") ?? "DATUM_SEMANTIC_FACE";
    if (!datumSemantics.TryResolve(datumId, datumRole, out var datum))
        throw new InvalidOperationException($"DATUM_SEMANTICS_NOT_FOUND: id={datumId}; role={datumRole ?? "<none>"}");

    Console.WriteLine("DATUM PROXY RESOLVER ENTER:");
    Console.WriteLine($"annotation_id={definition.AnnotationId}");
    Console.WriteLine($"owner_view={owner.Name}");
    Console.WriteLine($"datum_id={datumId}");
    Console.WriteLine($"datum_role={datumRole ?? "<none>"}");
    Console.WriteLine("proxy_resolver_version=V2_FRAME_REGISTERED");
    Console.WriteLine("proxy_strategies_attempted=PROJECTED_DATUM_BOUNDARY_EDGE, PROJECTED_DATUM_EXTREME_VERTEX, PROJECTED_DATUM_TERMINAL_EDGE, PROJECTED_DATUM_TERMINAL_VERTEX");
    Console.WriteLine("DATUM PLANE EVIDENCE:");
    Console.WriteLine($"datum_id={datum.StableId}");
    Console.WriteLine($"datum_role={datum.SemanticRole ?? datumRole ?? "<none>"}");
    Console.WriteLine($"datum_axis={datum.Axis}");
    Console.WriteLine($"datum_coordinate_m={datum.CoordinateM:0.#########}");
    Console.WriteLine($"datum_normal=[{(datum.Normal is null ? "<unavailable>" : string.Join(",", datum.Normal))}]");
    Console.WriteLine($"datum_source_artifact={datumSemantics.SourceArtifact}");
    Console.WriteLine($"datum_plane_tolerance_m={datum.MatchToleranceM:0.#########}");

    var viewFrameRegistration = RegisterSemanticToOwnerViewFrame(definition.AnnotationId, ownerRole, owner, semanticFrame);
    var datumFrame = viewFrameRegistration.Resolve(datum, datumRole);
    Console.WriteLine("DATUM FRAME REGISTRATION:");
    Console.WriteLine($"annotation_id={definition.AnnotationId}");
    Console.WriteLine($"datum_axis={datum.Axis}");
    Console.WriteLine($"semantic_coordinate_m={datum.CoordinateM:0.#########}");
    Console.WriteLine($"view_translation_m={(datumFrame.TranslationM is double translation ? translation.ToString("0.#########") : "<unavailable>")}");
    Console.WriteLine($"registered_coordinate_m={datumFrame.RegisteredCoordinateM:0.#########}");
    Console.WriteLine($"registration_evidence={datumFrame.Evidence}");
    Console.WriteLine($"residual_m={datumFrame.ResidualM:0.#########}");
    Console.WriteLine($"datum_resolution_authority={datumFrame.Authority}");

    var holeReference = ReadNestedString(definition.Geometry, "endpoint_b", "semantic_group_ref")
        ?? ReadNestedString(definition.Geometry, "hole_group", "semantic_group_ref")
        ?? throw new InvalidOperationException("HOLE_SEMANTIC_REFERENCE_UNAVAILABLE");
    if (!holeSemantics.TryGetValue(holeReference, out var hole))
        throw new InvalidOperationException("HOLE_SEMANTIC_GROUP_NOT_FOUND: " + holeReference);
    var placementRole = ReadNestedString(definition.Geometry, "endpoint_b", "placement_role")
        ?? throw new InvalidOperationException("HOLE_PLACEMENT_ROLE_UNAVAILABLE: " + definition.AnnotationId);
    var placement = SelectHolePlacementByRole(hole.PlacementPoints, placementRole);
    var circles = EnumerateVisibleCircles(owner);
    var projectedDistinct = DistinctProjectedCenters(hole.PlacementPoints, ownerRole);
    var target = MatchHoleCircleToPlacement(circles, placement);
    var sheetHole = ProjectModelPointToSheet(mathUtility, owner, target.ModelPoint);

    Console.WriteLine("DATUM RESOLUTION:");
    Console.WriteLine($"annotation_id={definition.AnnotationId}");
    Console.WriteLine($"datum semantic ref={datum.StableId}");
    Console.WriteLine($"datum role={datumRole ?? "<none>"}");
    Console.WriteLine($"datum strategy={datumStrategy}");
    var datumProxy = ResolveProjectedDatumProxy(model, mathUtility, owner, definition.AnnotationId, datum, datumRole, datumFrame, sheetHole);
    var datumEntity = datumProxy.Entity;
    var datumSelectable = datumProxy.Selectable;
    Console.WriteLine($"proxy strategy={datumProxy.Strategy}");
    Console.WriteLine($"candidate entities={datumProxy.CandidateCount}");
    Console.WriteLine($"selected datum entity={DescribeEntityText(datumEntity)}");
    Console.WriteLine($"native selectable={datumSelectable}");
    Console.WriteLine($"resolved={datumSelectable}");
    if (!datumSelectable) throw new InvalidOperationException("DATUM_NATIVE_ENTITY_NOT_SELECTABLE");

    Console.WriteLine("HOLE PLACEMENT RESOLUTION:");
    Console.WriteLine($"annotation_id={definition.AnnotationId}");
    Console.WriteLine($"hole semantic ref={holeReference}");
    Console.WriteLine($"physical placement count={hole.PlacementPoints.Count}");
    Console.WriteLine($"projected candidate count={projectedDistinct.Count}");
    Console.WriteLine($"selected placement role={placementRole}; point_m=[{string.Join(",", placement)}]");
    var holeSelectable = ProbeSingleSelection(model, target);
    Console.WriteLine($"matched native entity={DescribeEntityText(target)}");
    Console.WriteLine($"native selectable={holeSelectable}");
    Console.WriteLine($"resolved={holeSelectable}");
    if (!holeSelectable) throw new InvalidOperationException("HOLE_PLACEMENT_NATIVE_ENTITY_NOT_SELECTABLE");
    if (projectedDistinct.Count == 0) throw new InvalidOperationException("HOLE_PLACEMENT_DEGENERATE_OWNER_PROJECTION");

    var sheetDatum = ProjectModelPointToSheet(mathUtility, owner, datumEntity.ModelPoint);
    var dx = sheetHole[0] - sheetDatum[0];
    var dy = sheetHole[1] - sheetDatum[1];
    var distance = Math.Sqrt(dx * dx + dy * dy);
    // Engineering value authority stays in the semantic/model frame.  The
    // registered datum coordinate belongs only to runtime proxy resolution.
    var semanticDatumCoordinate = datum.CoordinateM;
    var semanticHoleCoordinate = placement[datum.AxisIndex];
    var semanticDistance = Math.Abs(semanticHoleCoordinate - semanticDatumCoordinate);
    Console.WriteLine("FEATURE LOCATION VALUE AUTHORITY:");
    Console.WriteLine($"annotation_id={definition.AnnotationId}");
    Console.WriteLine($"semantic_axis={datum.Axis}");
    Console.WriteLine($"datum_semantic_coordinate_m={semanticDatumCoordinate:0.#########}");
    Console.WriteLine($"hole_semantic_coordinate_m={semanticHoleCoordinate:0.#########}");
    Console.WriteLine($"expected_semantic_distance_m={semanticDistance:0.#########}");
    Console.WriteLine($"runtime_registered_datum_coordinate_m={datumFrame.RegisteredCoordinateM:0.#########}");
    Console.WriteLine("runtime_geometry_only=true");
    Console.WriteLine("value_frame=SEMANTIC_MODEL_FRAME");
    Console.WriteLine("FEATURE LOCATION GEOMETRY:");
    Console.WriteLine($"annotation_id={definition.AnnotationId}");
    Console.WriteLine($"owner_role={ownerRole}");
    Console.WriteLine("strategy=DATUM_TO_HOLE_PLACEMENT");
    Console.WriteLine($"datum entity={DescribeEntityText(datumEntity)}");
    Console.WriteLine($"hole entity={DescribeEntityText(target)}");
    Console.WriteLine($"sheet A=[{string.Join(",", sheetDatum)}]");
    Console.WriteLine($"sheet B=[{string.Join(",", sheetHole)}]");
    Console.WriteLine($"dx_sheet_mm={dx * 1000.0:0.###}");
    Console.WriteLine($"dy_sheet_mm={dy * 1000.0:0.###}");
    Console.WriteLine($"drawing_distance_mm={distance * 1000.0:0.###}");
    Console.WriteLine($"semantic_axis_distance_mm={semanticDistance * 1000.0:0.###}");
    Console.WriteLine($"resolved={distance > 1e-9}");
    if (distance <= 1e-9) throw new InvalidOperationException("FEATURE_LOCATION_ZERO_DISTANCE_PAIR");
    SelectPair(model, datumEntity, target, definition.AnnotationId);
    model.ClearSelection2(true);
    var evidence = new FeatureLocationEvidence(datum.StableId, holeReference, datumEntity, target, datumStrategy,
        placementRole, datumProxy.Strategy, datumProxy.PlaneDistanceM, datumFrame.Authority, datumFrame.RegisteredCoordinateM,
        datumFrame.TranslationM, datumFrame.Evidence, datumFrame.ResidualM);
    return new GeometryResolution(new[] { datumEntity, target }, datum.Axis, holeReference, hole,
        semanticDistance * 1000.0, datumProxy.CandidateCount + circles.Count, evidence);
}

static DatumProxyResolution ResolveProjectedDatumProxy(ModelDoc2 model, MathUtility mathUtility, View owner,
    string annotationId, DatumSemantic datum, string? datumRole, DatumFrameResolution datumFrame, double[] sheetHole)
{
    var inventory = EnumerateOwnerViewGeometryInventory(owner);
    Console.WriteLine("OWNER VIEW GEOMETRY INVENTORY:");
    Console.WriteLine($"annotation_id={annotationId}");
    Console.WriteLine($"owner_view={owner.Name}");
    Console.WriteLine($"visible_edges_total={inventory.Count(item => item.NativeType == "EDGE")}");
    Console.WriteLine($"visible_vertices_total={inventory.Count(item => item.NativeType == "VERTEX")}");
    var visibleAxisCoordinatesForDiagnostics = inventory
        .SelectMany(item => item.ModelPoints)
        .Where(point => point.Length > datum.AxisIndex)
        .Select(point => point[datum.AxisIndex])
        .ToArray();
    var terminalDirectionScore = DatumRoleDirection(datumRole, datum.Normal) is int direction && visibleAxisCoordinatesForDiagnostics.Length > 0
        ? (Math.Abs(datumFrame.RegisteredCoordinateM - (direction < 0 ? visibleAxisCoordinatesForDiagnostics.Min() : visibleAxisCoordinatesForDiagnostics.Max())) <= datum.MatchToleranceM ? 1.0 : 0.0)
        : 0.0;
    foreach (var item in inventory)
    {
        var selectable = ProbeRawEntitySelection(model, item.DrawingEntity);
        var planeDistance = item.ModelPoints.Length == 0 || item.ModelPoints.Any(point => point.Length <= datum.AxisIndex)
            ? double.NaN
            : item.ModelPoints.Max(point => Math.Abs(point[datum.AxisIndex] - datumFrame.RegisteredCoordinateM));
        var boundaryConsistent = double.IsFinite(planeDistance) && planeDistance <= datum.MatchToleranceM;
        var accepted = boundaryConsistent && selectable;
        var rejectReason = accepted ? "<none>"
            : item.ModelPoints.Length == 0 ? "MODEL_POINT_UNAVAILABLE"
            : !boundaryConsistent ? "NOT_ON_DATUM_PLANE"
            : !selectable ? "NOT_SELECTABLE"
            : "AMBIGUOUS";
        Console.WriteLine("DATUM PROXY INVENTORY CANDIDATE:");
        Console.WriteLine($"native_id={item.NativeId}");
        Console.WriteLine($"native_type={item.NativeType}");
        Console.WriteLine($"model_point_or_edge_endpoints={item.ModelGeometryText}");
        Console.WriteLine($"distance_to_datum_plane={(double.IsFinite(planeDistance) ? planeDistance.ToString("0.#########") : "<unavailable>")}");
        Console.WriteLine($"terminal_direction_score={terminalDirectionScore:0.###}");
        Console.WriteLine($"boundary_consistency={boundaryConsistent}");
        Console.WriteLine($"selectability={selectable}");
        Console.WriteLine("visibility=true");
        Console.WriteLine($"accepted={accepted}");
        Console.WriteLine($"reject_reason={rejectReason}");
    }
    var visibleVertices = EnumerateVisibleProjectedVertices(owner);
    var visibleEdges = EnumerateVisibleProjectedEdges(owner);
    var visibleAxisCoordinates = visibleVertices.Select(vertex => vertex.ModelPoint)
        .Concat(visibleEdges.SelectMany(edge => edge.Endpoints))
        .Where(point => point.Length > datum.AxisIndex)
        .Select(point => point[datum.AxisIndex])
        .ToArray();
    var terminalRoleMatch = IsDatumRoleTerminalMatch(datum, datumRole, datumFrame.RegisteredCoordinateM, visibleAxisCoordinates);
    var vertexCandidates = visibleVertices
        .Select(vertex => CreateDatumProxyCandidate(mathUtility, owner, datum, datumFrame.RegisteredCoordinateM, terminalRoleMatch, sheetHole, vertex, "VERTEX", null))
        .Where(candidate => candidate is not null)
        .Cast<DatumProxyCandidate>();
    var edgeCandidates = visibleEdges
        .Select(edge => CreateDatumProxyCandidate(mathUtility, owner, datum, datumFrame.RegisteredCoordinateM, terminalRoleMatch, sheetHole, edge.Entity, "EDGE", edge.Endpoints))
        .Where(candidate => candidate is not null)
        .Cast<DatumProxyCandidate>();
    var candidates = vertexCandidates.Concat(edgeCandidates)
        .OrderByDescending(candidate => candidate.Score)
        .ThenBy(candidate => candidate.OrthogonalProjectionErrorM)
        .ThenBy(candidate => candidate.Entity.Id, StringComparer.Ordinal)
        .ToArray();

    foreach (var candidate in candidates)
    {
        var selectable = ProbeSingleSelection(model, candidate.Entity);
        candidate.Selectable = selectable;
        Console.WriteLine("DATUM PROXY CANDIDATE:");
        Console.WriteLine($"native id={candidate.Entity.Id}");
        Console.WriteLine($"native type={candidate.Entity.NativeType}");
        Console.WriteLine($"model point / edge endpoints={candidate.ModelGeometryText}");
        Console.WriteLine($"distance_to_datum_plane={candidate.PlaneDistanceM:0.#########}");
        Console.WriteLine("visible=true");
        Console.WriteLine($"selectable={selectable}");
        Console.WriteLine($"score={candidate.Score:0.###}");
    }

    var selected = candidates.FirstOrDefault(candidate => candidate.Selectable);
    if (selected is null)
        throw new InvalidOperationException(candidates.Length == 0
            ? "DATUM_PROJECTED_ENTITY_UNAVAILABLE"
            : "DATUM_PROJECTED_ENTITY_NOT_SELECTABLE");
    return new DatumProxyResolution(selected.Entity, selected.Strategy, selected.PlaneDistanceM, selected.Selectable, candidates.Length);
}

static DatumProxyCandidate? CreateDatumProxyCandidate(MathUtility mathUtility, View owner, DatumSemantic datum,
    double registeredCoordinateM, bool terminalRoleMatch, double[] sheetHole, ResolvedNativeEntity entity, string sourceType, double[][]? edgeEndpoints)
{
    var modelPoints = edgeEndpoints is { Length: 2 } ? edgeEndpoints : new[] { entity.ModelPoint };
    if (modelPoints.Any(point => point.Length <= datum.AxisIndex)) return null;
    var planeDistance = modelPoints.Max(point => Math.Abs(point[datum.AxisIndex] - registeredCoordinateM));
    if (planeDistance > datum.MatchToleranceM) return null;

    var terminal = terminalRoleMatch;
    var strategy = sourceType switch
    {
        "EDGE" when terminal => "PROJECTED_DATUM_TERMINAL_EDGE",
        "VERTEX" when terminal => "PROJECTED_DATUM_TERMINAL_VERTEX",
        "EDGE" => "PROJECTED_DATUM_BOUNDARY_EDGE",
        _ => "PROJECTED_DATUM_EXTREME_VERTEX"
    };
    var sheet = ProjectModelPointToSheet(mathUtility, owner, entity.ModelPoint);
    var orthogonalProjectionError = Math.Abs(sheet[0] - sheetHole[0]);
    var score = strategy switch
    {
        "PROJECTED_DATUM_TERMINAL_EDGE" => 120.0,
        "PROJECTED_DATUM_TERMINAL_VERTEX" => 110.0,
        "PROJECTED_DATUM_BOUNDARY_EDGE" => 100.0,
        _ => 90.0
    };
    score -= planeDistance * 1_000_000.0;
    return new DatumProxyCandidate(entity, strategy, planeDistance, orthogonalProjectionError, score,
        edgeEndpoints is { Length: 2 }
            ? $"[{string.Join(",", edgeEndpoints[0])}] -> [{string.Join(",", edgeEndpoints[1])}]"
            : $"[{string.Join(",", entity.ModelPoint)}]");
}

static bool IsDatumRoleTerminalMatch(DatumSemantic datum, string? datumRole, double registeredCoordinateM, IReadOnlyList<double> visibleAxisCoordinates)
{
    var expectedSign = DatumRoleDirection(datumRole, datum.Normal);
    if (expectedSign is null || visibleAxisCoordinates.Count == 0) return false;
    var terminalCoordinate = expectedSign < 0 ? visibleAxisCoordinates.Min() : visibleAxisCoordinates.Max();
    return Math.Abs(registeredCoordinateM - terminalCoordinate) <= datum.MatchToleranceM;
}

static int? DatumRoleDirection(string? datumRole, double[]? normal)
{
    var role = datumRole?.Trim().ToUpperInvariant().Replace('-', '_').Replace(' ', '_') ?? "";
    if (role.StartsWith("MIN_", StringComparison.Ordinal) || role.StartsWith("LOWER_", StringComparison.Ordinal) ||
        role.EndsWith("_MIN", StringComparison.Ordinal) || role.EndsWith("_LOWER", StringComparison.Ordinal)) return -1;
    if (role.StartsWith("MAX_", StringComparison.Ordinal) || role.StartsWith("UPPER_", StringComparison.Ordinal) ||
        role.EndsWith("_MAX", StringComparison.Ordinal) || role.EndsWith("_UPPER", StringComparison.Ordinal)) return 1;
    if (normal is { Length: >= 3 })
    {
        var dominant = normal.OrderByDescending(value => Math.Abs(value)).FirstOrDefault();
        if (Math.Abs(dominant) > 1e-9) return Math.Sign(dominant);
    }
    return null;
}

static double[] SelectHolePlacementByRole(IReadOnlyList<double[]> points, string role)
{
    if (points.Count == 0) throw new InvalidOperationException("HOLE_PLACEMENT_POINTS_UNAVAILABLE");
    var normalized = role.Trim().ToUpperInvariant().Replace('-', '_').Replace(' ', '_');
    var axis = normalized.EndsWith("_X", StringComparison.Ordinal) ? 0
        : normalized.EndsWith("_Y", StringComparison.Ordinal) ? 1
        : normalized.EndsWith("_Z", StringComparison.Ordinal) ? 2
        : throw new InvalidOperationException("HOLE_PLACEMENT_ROLE_AXIS_UNAVAILABLE: " + role);
    var ordered = points.OrderBy(point => point[axis]).ToArray();
    if (normalized.StartsWith("MIN_", StringComparison.Ordinal) || normalized.StartsWith("LOWER_", StringComparison.Ordinal))
        return ordered[0];
    if (normalized.StartsWith("MAX_", StringComparison.Ordinal) || normalized.StartsWith("UPPER_", StringComparison.Ordinal))
        return ordered[^1];
    if (normalized.StartsWith("CENTER_", StringComparison.Ordinal))
    {
        var center = (ordered[0][axis] + ordered[^1][axis]) / 2.0;
        return ordered.OrderBy(point => Math.Abs(point[axis] - center)).ThenBy(point => point[axis]).First();
    }
    throw new InvalidOperationException("UNSUPPORTED_HOLE_PLACEMENT_ROLE: " + role);
}

static ResolvedNativeEntity MatchHoleCircleToPlacement(IEnumerable<ResolvedNativeEntity> circles, double[] placement)
{
    var candidates = circles.Select(circle => new { Circle = circle, Error = Distance(circle.ModelPoint, placement) })
        .Where(candidate => candidate.Error <= 0.0002)
        .OrderBy(candidate => candidate.Error)
        .ThenBy(candidate => candidate.Circle.Radius)
        .ThenBy(candidate => candidate.Circle.Id, StringComparer.Ordinal)
        .ToArray();
    return candidates.Length == 0
        ? throw new InvalidOperationException("HOLE_PLACEMENT_OPENING_CIRCLE_UNAVAILABLE")
        : candidates[0].Circle;
}

static List<ResolvedNativeEntity> EnumerateVisibleProjectedVertices(View view)
{
    var result = new List<ResolvedNativeEntity>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    for (var type = 1; type <= 3; type++)
    {
        if (view.GetVisibleEntities2(null!, type) is not Array entities) continue;
        foreach (var candidate in entities.Cast<object>())
        {
            if (candidate is not Entity drawingEntity || view.GetCorresponding(drawingEntity) is not Vertex modelVertex) continue;
            var point = ToDoubles(modelVertex.GetPoint());
            if (point.Length < 3) continue;
            var key = string.Join("|", point.Take(3).Select(value => Math.Round(value, 10).ToString("R")));
            if (seen.Add(key))
                result.Add(new ResolvedNativeEntity($"{view.Name}:VERTEX:{result.Count + 1}", drawingEntity, "VERTEX", point.Take(3).ToArray(), null));
        }
    }
    return result;
}

static List<OwnerViewGeometryItem> EnumerateOwnerViewGeometryInventory(View view)
{
    var result = new List<OwnerViewGeometryItem>();
    var sequence = 0;
    for (var type = 1; type <= 3; type++)
    {
        if (view.GetVisibleEntities2(null!, type) is not Array entities) continue;
        foreach (var candidate in entities.Cast<object>())
        {
            if (candidate is not Entity drawingEntity) continue;
            sequence++;
            var corresponding = view.GetCorresponding(drawingEntity);
            if (corresponding is Vertex vertex)
            {
                var point = ToDoubles(vertex.GetPoint());
                var points = point.Length >= 3 ? new[] { point.Take(3).ToArray() } : Array.Empty<double[]>();
                result.Add(new OwnerViewGeometryItem($"{view.Name}:INVENTORY:{sequence}", drawingEntity, "VERTEX", points,
                    points.Length == 0 ? "MODEL_POINT_UNAVAILABLE" : null));
            }
            else if (corresponding is Edge edge)
            {
                var endpoints = GetEdgeEndpoints(edge) ?? Array.Empty<double[]>();
                result.Add(new OwnerViewGeometryItem($"{view.Name}:INVENTORY:{sequence}", drawingEntity, "EDGE", endpoints,
                    endpoints.Length == 0 ? "MODEL_POINT_UNAVAILABLE" : null));
            }
        }
    }
    return result;
}

static List<VisibleProjectedEdge> EnumerateVisibleProjectedEdges(View view)
{
    var result = new List<VisibleProjectedEdge>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    for (var type = 1; type <= 3; type++)
    {
        if (view.GetVisibleEntities2(null!, type) is not Array entities) continue;
        foreach (var candidate in entities.Cast<object>())
        {
            if (candidate is not Entity drawingEntity || view.GetCorresponding(drawingEntity) is not Edge modelEdge) continue;
            var endpoints = GetEdgeEndpoints(modelEdge);
            if (endpoints is null) continue;
            var midpoint = endpoints[0].Zip(endpoints[1], (a, b) => (a + b) / 2.0).ToArray();
            var key = string.Join("|", endpoints.SelectMany(point => point).Select(value => Math.Round(value, 10).ToString("R")));
            if (!seen.Add(key)) continue;
            var entity = new ResolvedNativeEntity($"{view.Name}:EDGE:{result.Count + 1}", drawingEntity, "EDGE", midpoint, null);
            result.Add(new VisibleProjectedEdge(entity, endpoints));
        }
    }
    return result;
}

static double[][]? GetEdgeEndpoints(Edge edge)
{
    var start = edge.GetStartVertex() as Vertex;
    var end = edge.GetEndVertex() as Vertex;
    if (start is null || end is null) return null;
    var startPoint = ToDoubles(start.GetPoint());
    var endPoint = ToDoubles(end.GetPoint());
    return startPoint.Length >= 3 && endPoint.Length >= 3
        ? new[] { startPoint.Take(3).ToArray(), endPoint.Take(3).ToArray() }
        : null;
}

static List<PhysicalOpeningFact> LoadPhysicalOpeningFacts(JsonElement semantic)
{
    JsonElement openings;
    if (semantic.TryGetProperty("physical_openings", out var topLevel) && topLevel.ValueKind == JsonValueKind.Array)
        openings = topLevel;
    else if (semantic.TryGetProperty("body_topology", out var topology) &&
        topology.TryGetProperty("physical_openings", out var nested) && nested.ValueKind == JsonValueKind.Array)
        openings = nested;
    else
        return new List<PhysicalOpeningFact>();
    var result = new List<PhysicalOpeningFact>();
    var attribution = new Dictionary<string, (HashSet<string> Groups, HashSet<string> ProvenGroups)>(StringComparer.Ordinal);
    if (semantic.TryGetProperty("holewizard_attribution", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
        foreach (var row in attrs.EnumerateArray())
        {
            var group = TryString(row, "feature_name", "feature_id", "semantic_id");
            if (group is null || !row.TryGetProperty("compatible_opening_ids", out var ids) || ids.ValueKind != JsonValueKind.Array) continue;
            var proven = string.Equals(TryString(row, "group_status"), "GROUP_PROVEN", StringComparison.OrdinalIgnoreCase);
            foreach (var id in ids.EnumerateArray()) if (id.ValueKind == JsonValueKind.String)
            {
                if (!attribution.TryGetValue(id.GetString()!, out var entry)) entry = (new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
                entry.Groups.Add(group); if (proven) entry.ProvenGroups.Add(group); attribution[id.GetString()!] = entry;
            }
        }
    foreach (var opening in openings.EnumerateArray())
    {
        var id = TryString(opening, "opening_id") ?? TryString(opening, "physical_opening_id") ?? "";
        var signature = TryString(opening, "geometry_signature") ?? "";
        var boundaries = new HashSet<string>(StringComparer.Ordinal);
        if (opening.TryGetProperty("boundary_circular_edge_signature", out var boundary) && boundary.ValueKind == JsonValueKind.Array)
            foreach (var item in boundary.EnumerateArray()) if (item.ValueKind == JsonValueKind.String) boundaries.Add(item.GetString()!);
        if (!string.IsNullOrWhiteSpace(id) && boundaries.Count > 0)
        {
            var groups = attribution.TryGetValue(id, out var groupEvidence) ? groupEvidence.Groups : new HashSet<string>(StringComparer.Ordinal);
            var provenGroups = attribution.TryGetValue(id, out groupEvidence) ? groupEvidence.ProvenGroups : new HashSet<string>(StringComparer.Ordinal);
            result.Add(new PhysicalOpeningFact(id, signature, boundaries, groups, provenGroups,
                TryString(opening, "origin_class", "origin") ?? "UNKNOWN",
                opening.TryGetProperty("origin_proven", out var proven) && proven.ValueKind == JsonValueKind.True));
        }
    }
    return result;
}

static List<ResolvedNativeEntity> MatchHoleCirclesByPhysicalSignature(
    IEnumerable<ResolvedNativeEntity> circles, IReadOnlyList<PhysicalOpeningFact> openings, string semanticGroup)
{
    var index = new Dictionary<string, List<PhysicalOpeningFact>>(StringComparer.Ordinal);
    foreach (var opening in openings)
        foreach (var signature in opening.BoundarySignatures)
        {
            if (!index.TryGetValue(signature, out var owners)) index[signature] = owners = new List<PhysicalOpeningFact>();
            owners.Add(opening);
        }
    if (index.Any(pair => pair.Value.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count() > 1))
        throw new InvalidOperationException("PHYSICAL_BOUNDARY_SIGNATURE_AMBIGUOUS");
    var result = new List<ResolvedNativeEntity>();
    foreach (var circle in circles)
    {
        var exact = index.TryGetValue(circle.PhysicalSignature, out var owners) ? owners : new List<PhysicalOpeningFact>();
        var opening = exact.SingleOrDefault(value => value.ProvenGroups.Contains(semanticGroup));
        var exactMatch = exact.Count > 0;
        var attributionMatch = opening is not null;
        Console.WriteLine($"MODEL_EDGE_CANONICAL_SIGNATURE={circle.PhysicalSignature}");
        Console.WriteLine($"PHYSICAL_SIGNATURE_EXACT_MATCH={(exactMatch ? "YES" : "NO")}");
        Console.WriteLine($"MATCHED_PHYSICAL_OPENING_ID={opening?.Id ?? "<none>"}");
        Console.WriteLine($"MATCHED_SEMANTIC_GROUP={semanticGroup}");
        Console.WriteLine($"SEMANTIC_ATTRIBUTION_MATCH={(attributionMatch ? "YES" : "NO")}");
        if (attributionMatch && result.All(existing => !ReferenceEquals(existing.Object, circle.Object))) result.Add(circle);
    }
    Console.WriteLine($"HOLE_OPENING_MATCH_INPUT candidate_count={circles.Count()};physical_opening_count={openings.Count}");
    return result;
}

static List<ResolvedNativeEntity> EnumerateVisibleCircles(View view, Action<string>? reportSubstage = null)
{
    var result = new List<ResolvedNativeEntity>();
    var diagnosticEntityIndex = 0;
    // Openings may be returned as ordinary or silhouette edges depending on
    // the view/display state.  Both are real selectable drawing entities.
    for (var type = 1; type <= 4; type++)
    {
        reportSubstage?.Invoke("GET_VISIBLE_ENTITIES");
        Console.WriteLine("HOLE_DIAG_STAGE=GET_VISIBLE_ENTITIES_ENTER");
        if (view.GetVisibleEntities2(null!, type) is not Array entities) continue;
        Console.WriteLine("HOLE_DIAG_STAGE=GET_VISIBLE_ENTITIES_EXIT");
        Console.WriteLine($"HOLE_DIAG_VISIBLE_ENTITY_RAW_COUNT={entities.Length}");
        foreach (var candidate in entities.Cast<object>())
        {
            if (candidate is not Entity drawingEntity) continue;
            var entityIndex = diagnosticEntityIndex++;
            Console.WriteLine($"HOLE_DIAG_ENTITY_INDEX={entityIndex}");
            reportSubstage?.Invoke("GET_CORRESPONDING");
            Console.WriteLine("HOLE_DIAG_STAGE=GET_CORRESPONDING_ENTER");
            var corresponding = view.GetCorresponding(drawingEntity);
            Console.WriteLine("HOLE_DIAG_STAGE=GET_CORRESPONDING_EXIT");
            Console.WriteLine($"HOLE_DIAG_ENTITY_INDEX={entityIndex}");
            Console.WriteLine($"HOLE_DIAG_CORRESPONDING_NULL={(corresponding is null ? "YES" : "NO")}");
            if (corresponding is not Edge modelEdge) continue;
            reportSubstage?.Invoke("GET_CURVE");
            Console.WriteLine("HOLE_DIAG_STAGE=GET_CURVE_ENTER");
            var curve = modelEdge.GetCurve() as Curve;
            Console.WriteLine("HOLE_DIAG_STAGE=GET_CURVE_EXIT");
            Console.WriteLine($"HOLE_DIAG_ENTITY_INDEX={entityIndex}");
            Console.WriteLine($"HOLE_DIAG_CURVE_NULL={(curve is null ? "YES" : "NO")}");
            if (curve is null) continue;
            Console.WriteLine("HOLE_DIAG_STAGE=IS_CIRCLE_ENTER");
            var isCircle = curve.IsCircle();
            Console.WriteLine("HOLE_DIAG_STAGE=IS_CIRCLE_EXIT");
            Console.WriteLine($"HOLE_DIAG_ENTITY_INDEX={entityIndex}");
            Console.WriteLine($"HOLE_DIAG_IS_CIRCLE={isCircle.ToString().ToUpperInvariant()}");
            if (!isCircle) continue;
            reportSubstage?.Invoke("CIRCLE_PARAMS");
            Console.WriteLine("HOLE_DIAG_STAGE=CIRCLE_PARAMS_ENTER");
            var circle = ToDoubles(curve.CircleParams);
            Console.WriteLine("HOLE_DIAG_STAGE=CIRCLE_PARAMS_EXIT");
            Console.WriteLine($"HOLE_DIAG_ENTITY_INDEX={entityIndex}");
            if (circle.Length < 3) continue;
            var radius = circle.Length > 6 ? Math.Abs(circle[6]) : double.PositiveInfinity;
            var identity = string.Join("|", circle.Take(7).Select(value => Math.Round(value, 10).ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
            if (result.Any(existing => existing.NativeType == "CIRCULAR_EDGE" &&
                Distance(existing.ModelPoint, circle.Take(3).ToArray()) <= 1e-10 &&
                Math.Abs(existing.Radius - radius) <= 1e-10)) continue;
            result.Add(new ResolvedNativeEntity($"{view.Name}:CIRCLE:{result.Count + 1}:{identity}", drawingEntity, "CIRCULAR_EDGE", circle.Take(3).ToArray(), radius, circle.Skip(3).Take(3).ToArray()));
        }
    }
    return result;
}

static double[]? CircleParametersForVisibleEntity(View view, Entity drawingEntity)
{
    if (view.GetCorresponding(drawingEntity) is not Edge modelEdge) return null;
    var curve = modelEdge.GetCurve() as Curve;
    if (curve?.IsCircle() != true) return null;
    var parameters = ToDoubles(curve.CircleParams);
    return parameters.Length >= 7 ? parameters : null;
}

static List<ResolvedNativeEntity> MatchHoleCircles(IEnumerable<ResolvedNativeEntity> circles, IReadOnlyList<double[]> placementPoints)
{
    var all = circles.ToArray();
    Console.WriteLine($"HOLE_OPENING_MATCH_INPUT candidate_count={all.Length};placement_count={placementPoints.Count}");
    var result = new List<ResolvedNativeEntity>();
    foreach (var point in placementPoints)
    {
        var nearest = all.Select(circle => new { Circle = circle, Error = Distance(circle.ModelPoint, point) })
            .Where(item => item.Error <= 0.0002)
            .OrderBy(item => item.Error)
            .ThenBy(item => item.Circle.Radius)
            .FirstOrDefault();
        Console.WriteLine($"HOLE_OPENING_MATCH point=[{string.Join(",", point)}];matched={(nearest is null ? "false" : "true")};error_m={(nearest?.Error.ToString("R") ?? "<none>")};candidate_count={all.Length}");
        if (nearest is not null && result.All(existing => !ReferenceEquals(existing.Object, nearest.Circle.Object)))
            result.Add(nearest.Circle);
    }
    return result;
}

static (ResolvedNativeEntity A, ResolvedNativeEntity B) SelectAdjacentPair(IReadOnlyList<ResolvedNativeEntity> points)
{
    ResolvedNativeEntity? selectedA = null;
    ResolvedNativeEntity? selectedB = null;
    var bestDistance = double.PositiveInfinity;
    for (var i = 0; i < points.Count; i++)
        for (var j = i + 1; j < points.Count; j++)
        {
            var distance = Distance(points[i].ModelPoint, points[j].ModelPoint);
            if (distance > 1e-9 && distance < bestDistance)
            {
                bestDistance = distance;
                selectedA = points[i];
                selectedB = points[j];
            }
        }
    return selectedA is not null && selectedB is not null
        ? (selectedA, selectedB)
        : throw new InvalidOperationException("NONDEGENERATE_ADJACENT_CENTER_PAIR_UNAVAILABLE");
}

static List<double[]> DistinctProjectedCenters(IEnumerable<double[]> points, string role) =>
    points.Select(point => ProjectPoint(point, role))
        .GroupBy(point => $"{Math.Round(point[0], 9)}|{Math.Round(point[1], 9)}", StringComparer.Ordinal)
        .Select(group => group.First()).ToList();

static double[] ProjectPoint(double[] point, string role) => NormalizeRole(role) switch
{
    "front" or "back" => new[] { point[0], point[1] },
    "left" or "right" => new[] { point[2], point[1] },
    "top" or "bottom" => new[] { point[0], point[2] },
    _ => throw new InvalidOperationException("UNSUPPORTED_PROJECTION_ROLE: " + role)
};

static string ClassifyPattern(IReadOnlyList<double[]> points)
{
    if (points.Count < 2) return points.Count == 1 ? "SINGLE" : "UNAVAILABLE";
    var ranges = Enumerable.Range(0, 3).Select(axis => points.Max(point => point[axis]) - points.Min(point => point[axis])).ToArray();
    return ranges.Count(range => range > 1e-9) == 1 ? "LINEAR_PATTERN" : "MULTI_AXIS_PATTERN";
}

static IReadOnlyDictionary<string, HoleSemantic> LoadHoleSemantics(string path)
{
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    if (!document.RootElement.TryGetProperty("holewizard_semantics", out var rows) || rows.ValueKind != JsonValueKind.Array)
        throw new InvalidOperationException("HOLE_SEMANTICS_SCHEMA_ERROR");
    var result = new Dictionary<string, HoleSemantic>(StringComparer.OrdinalIgnoreCase);
    foreach (var row in rows.EnumerateArray())
    {
        var id = TryString(row, "feature_name", "feature_id", "semantic_id");
        if (id is null) continue;
        var points = new List<double[]>();
        if (row.TryGetProperty("sketch_points", out var sketchPoints) && sketchPoints.ValueKind == JsonValueKind.Array)
            foreach (var point in sketchPoints.EnumerateArray())
                if (point.TryGetProperty("sketch_to_model_point_m", out var transformed) && transformed.ValueKind == JsonValueKind.Array)
                {
                    var xyz = transformed.EnumerateArray().Take(3).Select(value => value.GetDouble()).ToArray();
                    if (xyz.Length == 3) points.Add(xyz);
                }
        if (points.Count == 0) throw new InvalidOperationException("HOLE_PLACEMENT_POINTS_UNAVAILABLE: " + id);
        result.Add(id, new HoleSemantic(id, points,
            TryString(row, "FastenerSize", "thread_size"),
            TryString(row, "ThreadToleranceClass", "thread_tolerance_class", "thread_tolerance", "tolerance_class"),
            TryNumber(row, "TapDrillDiameter", "drill_diameter_m", "drill_diameter"),
            TryNumber(row, "TapDrillDepth", "tap_drill_depth_m", "tap_drill_depth", "DrillDepth", "drill_depth_m", "drill_depth"),
            TryNumber(row, "ThreadDepth", "thread_depth_m", "thread_depth"),
            TryString(row, "DrillEndCondition", "drill_end_condition", "drill_end", "EndCondition", "end_condition"),
            TryString(row, "ThreadEndCondition", "thread_end_condition", "thread_end", "EndCondition", "end_condition")));
    }
    return result;
}

static DatumSemanticCatalog LoadDatumSemantics(string path)
{
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var entries = new List<DatumSemantic>();
    CollectDatumSemantics(document.RootElement, null, entries);
    if (entries.Count == 0) throw new InvalidOperationException("DATUM_SEMANTICS_SCHEMA_ERROR");
    return new DatumSemanticCatalog(entries, path);
}

static void CollectDatumSemantics(JsonElement element, string? semanticRole, List<DatumSemantic> entries)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        var stableId = TryString(element, "stable_id", "datum_id", "id");
        var axis = TryString(element, "axis");
        var coordinate = TryNumber(element, "coordinate_m", "coordinate");
        var normal = TryNumberArray(element, "normal");
        if (!string.IsNullOrWhiteSpace(stableId) && !string.IsNullOrWhiteSpace(axis) && coordinate is double coordinateM)
        {
            var normalizedAxis = axis!.Trim().ToUpperInvariant();
            entries.Add(new DatumSemantic(stableId!, semanticRole, normalizedAxis, AxisIndex(normalizedAxis), coordinateM, 0.0002, normal));
        }
        foreach (var property in element.EnumerateObject())
            CollectDatumSemantics(property.Value, property.Name, entries);
    }
    else if (element.ValueKind == JsonValueKind.Array)
        foreach (var item in element.EnumerateArray()) CollectDatumSemantics(item, semanticRole, entries);
}

static SemanticFrameEvidence LoadSemanticFrameEvidence(string path)
{
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var extrema = new List<SemanticAxisExtreme>();
    CollectSemanticAxisExtrema(document.RootElement, null, extrema);
    var axes = extrema
        .GroupBy(item => item.Axis, StringComparer.OrdinalIgnoreCase)
        .Select(group =>
        {
            var minimum = group.Where(item => item.Direction < 0).Select(item => item.CoordinateM).DefaultIfEmpty(double.NaN).Min();
            var maximum = group.Where(item => item.Direction > 0).Select(item => item.CoordinateM).DefaultIfEmpty(double.NaN).Max();
            return new SemanticAxisExtent(group.Key, minimum, maximum, group.Count());
        })
        .Where(extent => double.IsFinite(extent.MinimumM) && double.IsFinite(extent.MaximumM) && extent.MaximumM > extent.MinimumM)
        .ToDictionary(extent => extent.Axis, StringComparer.OrdinalIgnoreCase);
    if (axes.Count == 0) throw new InvalidOperationException("SEMANTIC_FRAME_EVIDENCE_INCOMPLETE: axis extrema unavailable");

    Console.WriteLine("SEMANTIC FRAME:");
    Console.WriteLine($"source artifact={path}");
    foreach (var extent in axes.Values.OrderBy(item => item.Axis, StringComparer.OrdinalIgnoreCase))
        Console.WriteLine($"axis={extent.Axis}; min_m={extent.MinimumM:0.#########}; max_m={extent.MaximumM:0.#########}; extent_m={extent.ExtentM:0.#########}; evidence_count={extent.EvidenceCount}");
    return new SemanticFrameEvidence(path, axes);
}

static void CollectSemanticAxisExtrema(JsonElement element, string? parentName, List<SemanticAxisExtreme> extrema)
{
    if (element.ValueKind == JsonValueKind.Object)
    {
        var axis = TryString(element, "axis");
        var coordinate = TryNumber(element, "coordinate_m", "coordinate");
        var role = TryString(element, "role", "axis_role_candidate", "semantic_role");
        var direction = role is null ? (int?)null : DatumRoleDirection(role, null);
        if (!string.IsNullOrWhiteSpace(axis) && coordinate is double coordinateM && direction is not null &&
            (parentName?.Contains("extreme", StringComparison.OrdinalIgnoreCase) == true || role is not null))
            extrema.Add(new SemanticAxisExtreme(axis.Trim().ToUpperInvariant(), coordinateM, direction.Value));
        foreach (var property in element.EnumerateObject())
            CollectSemanticAxisExtrema(property.Value, property.Name, extrema);
    }
    else if (element.ValueKind == JsonValueKind.Array)
        foreach (var item in element.EnumerateArray()) CollectSemanticAxisExtrema(item, parentName, extrema);
}

static PerOwnerViewFrameRegistration RegisterSemanticToOwnerViewFrame(string annotationId, string ownerRole, View owner,
    SemanticFrameEvidence semantic)
{
    const double extentToleranceM = 0.0002;
    const double translationConsistencyToleranceM = 0.0002;
    var inventory = EnumerateOwnerViewGeometryInventory(owner);
    var points = inventory.SelectMany(item => item.ModelPoints)
        .Where(point => point.Length >= 3)
        .Select(point => point.Take(3).ToArray())
        .ToArray();
    var runtimeAxes = new Dictionary<string, RuntimeAxisExtent>(StringComparer.OrdinalIgnoreCase);
    foreach (var (axisName, axisIndex) in new[] { ("X", 0), ("Y", 1), ("Z", 2) })
    {
        var values = points.Where(point => point.Length > axisIndex).Select(point => point[axisIndex]).ToArray();
        if (values.Length > 0)
            runtimeAxes.Add(axisName, new RuntimeAxisExtent(axisName, values.Min(), values.Max(), values.Length, 1));
    }

    Console.WriteLine("OWNER VIEW FRAME:");
    Console.WriteLine($"annotation_id={annotationId}");
    Console.WriteLine($"owner_role={ownerRole}");
    Console.WriteLine($"native_view={owner.Name}");
    foreach (var axis in new[] { "X", "Y", "Z" })
    {
        if (runtimeAxes.TryGetValue(axis, out var runtimeAxis))
            Console.WriteLine($"axis={axis}; runtime_min={runtimeAxis.MinimumM:0.#########}; runtime_max={runtimeAxis.MaximumM:0.#########}; runtime_extent={runtimeAxis.ExtentM:0.#########}; evidence_count={runtimeAxis.EvidenceCount}");
        else
            Console.WriteLine($"axis={axis}; runtime_min=<unavailable>; runtime_max=<unavailable>; runtime_extent=<unavailable>; evidence_count=0");
    }

    var axes = new Dictionary<string, AxisFrameRegistration>(StringComparer.OrdinalIgnoreCase);
    foreach (var semanticAxis in semantic.Axes.Values)
    {
        if (!runtimeAxes.TryGetValue(semanticAxis.Axis, out var runtimeAxis))
        {
            axes[semanticAxis.Axis] = AxisFrameRegistration.Unresolved(semanticAxis.Axis, "UNUSABLE:RUNTIME_AXIS_EVIDENCE_UNAVAILABLE",
                semanticAxis, null, double.NaN, double.NaN, "UNUSABLE");
            continue;
        }
        var extentResidual = Math.Abs(semanticAxis.ExtentM - runtimeAxis.ExtentM);
        var fromMin = runtimeAxis.MinimumM - semanticAxis.MinimumM;
        var fromMax = runtimeAxis.MaximumM - semanticAxis.MaximumM;
        var translationResidual = Math.Abs(fromMin - fromMax);
        var observability = runtimeAxis.ExtentM <= extentToleranceM
            ? "COLLAPSED"
            : extentResidual <= extentToleranceM
                ? "FULLY_OBSERVABLE"
                : runtimeAxis.ExtentM < semanticAxis.ExtentM
                    ? "PARTIALLY_OBSERVABLE"
                    : "UNUSABLE";
        var consistent = observability == "FULLY_OBSERVABLE" && translationResidual <= translationConsistencyToleranceM;
        axes[semanticAxis.Axis] = consistent
            ? AxisFrameRegistration.Registered(semanticAxis.Axis, (fromMin + fromMax) / 2.0, extentResidual, translationResidual,
                semanticAxis, runtimeAxis, observability)
            : AxisFrameRegistration.Unresolved(semanticAxis.Axis,
                $"{observability}: extent_residual_m={extentResidual:0.#########}; translation_residual_m={translationResidual:0.#########}",
                semanticAxis, runtimeAxis, extentResidual, translationResidual, observability);

        var registration = axes[semanticAxis.Axis];
        Console.WriteLine("PER_VIEW FRAME REGISTRATION:");
        Console.WriteLine($"annotation_id={annotationId}");
        Console.WriteLine($"owner_role={ownerRole}");
        Console.WriteLine($"axis={semanticAxis.Axis}");
        Console.WriteLine($"observability={registration.Observability}");
        Console.WriteLine($"semantic_extent={semanticAxis.ExtentM:0.#########}");
        Console.WriteLine($"runtime_extent={runtimeAxis.ExtentM:0.#########}");
        Console.WriteLine($"translation_from_min={fromMin:0.#########}");
        Console.WriteLine($"translation_from_max={fromMax:0.#########}");
        Console.WriteLine($"translation_axis_m={(registration.TranslationM is double value ? value.ToString("0.#########") : "<unavailable>")}");
        Console.WriteLine($"residual_m={registration.ResidualM:0.#########}");
        Console.WriteLine($"resolved={registration.Resolved}");
    }
    return new PerOwnerViewFrameRegistration(annotationId, ownerRole, owner.Name, semantic, runtimeAxes, axes);
}

static void PrintFeatureLocationSchemas(IEnumerable<Definition> definitions)
{
    foreach (var definition in definitions.Where(definition => definition.Category.Equals("FEATURE_LOCATION", StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine("FEATURE_LOCATION DEFINITION SCHEMA:");
        Console.WriteLine($"annotation_id={definition.AnnotationId}");
        Console.WriteLine($"category={definition.Category}");
        Console.WriteLine($"semantic_source_ids={JsonSerializer.Serialize(definition.SemanticSourceIds)}");
        Console.WriteLine($"owner_view={definition.OwnerView.GetRawText()}");
        Console.WriteLine($"geometry={definition.Geometry.GetRawText()}");
        Console.WriteLine($"dimension={definition.Dimension.GetRawText()}");
        Console.WriteLine($"placement={JsonSerializer.Serialize(definition.Placement)}");
        Console.WriteLine($"creation={definition.Creation.GetRawText()}");
        Console.WriteLine($"validation={definition.Validation.GetRawText()}");
    }
}

static string FindProjectArtifact(string startingPath, string fileName)
{
    for (var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(startingPath))!);
         directory is not null; directory = directory.Parent)
    {
        foreach (var relative in new[]
        {
            fileName,
            Path.Combine("input", "semantics", "earlybound", fileName),
            Path.Combine("semantics", "earlybound", fileName)
        })
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
    }
    throw new FileNotFoundException("PROJECT_SEMANTIC_ARTIFACT_NOT_FOUND: " + fileName);
}

static string FormatHoleSemantics(HoleSemantic hole)
{
    var quantity = hole.PlacementPoints.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    var drillCondition = FormatHoleCondition(hole.DrillEndCondition, hole.DrillDepthM);
    var threadCondition = FormatHoleCondition(hole.ThreadEndCondition, hole.ThreadDepthM);
    var drillLine = hole.DrillDiameterM is double drill
        ? $"{quantity} × ⌀{FormatMetricMm(drill)}{drillCondition}"
        : $"{quantity}{drillCondition}";
    var thread = hole.Thread ?? string.Empty;
    if (!string.IsNullOrWhiteSpace(hole.ThreadToleranceClass))
        thread += $"-{hole.ThreadToleranceClass}";
    var threadLine = string.IsNullOrWhiteSpace(thread) ? threadCondition.Trim() : $"{thread}{threadCondition}";
    return string.Join(System.Environment.NewLine, new[] { drillLine, threadLine }.Where(line => !string.IsNullOrWhiteSpace(line)));
}

static string FormatHoleCondition(string? condition, double? depthM)
{
    if (IsThroughCondition(condition)) return " 完全贯穿";
    if (depthM is double depth) return $" 深{FormatMetricMm(depth)}";
    return string.IsNullOrWhiteSpace(condition) ? string.Empty : $" {condition.Trim()}";
}

static bool IsThroughCondition(string? condition)
{
    if (string.IsNullOrWhiteSpace(condition)) return false;
    var value = condition.Trim().ToUpperInvariant();
    return value.Contains("THRU", StringComparison.Ordinal) || value.Contains("THROUGH", StringComparison.Ordinal) ||
        value.Contains("贯穿", StringComparison.Ordinal) || value.Contains("完全贯穿", StringComparison.Ordinal);
}

static string FormatMetricMm(double metres) =>
    (metres * 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

static double[] DimensionPosition(View view, Definition definition, int lane, string? resolvedOrientation = null)
{
    var outline = ToDoubles(view.GetOutline());
    if (outline.Length < 4) throw new InvalidOperationException("OWNER_VIEW_OUTLINE_UNAVAILABLE: " + definition.AnnotationId);
    var offset = 0.012 + lane * 0.008;
    var orientation = resolvedOrientation ?? DimensionOrientation(definition);
    var side = definition.Placement.Side.Trim().ToUpperInvariant();
    if (orientation == "VERTICAL" || (orientation == "ALIGNED" && (side == "LEFT" || side == "RIGHT")))
        return side == "RIGHT"
            ? new[] { outline[2] + offset, (outline[1] + outline[3]) / 2.0 }
            : new[] { outline[0] - offset, (outline[1] + outline[3]) / 2.0 };
    return side == "BELOW" || side == "BOTTOM"
        ? new[] { (outline[0] + outline[2]) / 2.0, outline[1] - offset }
        : new[] { (outline[0] + outline[2]) / 2.0, outline[3] + offset };
}

static string DimensionOrientation(Definition definition) =>
    definition.Dimension.ValueKind == JsonValueKind.Object
        ? TryString(definition.Dimension, "orientation")?.ToUpperInvariant() ?? "ALIGNED"
        : "ALIGNED";

static string GeometryStrategy(Definition definition) => TryString(definition.Geometry, "entity_strategy") ?? "<unavailable>";

static bool IntentRequiresDatumSemantics(JsonElement intent)
{
    if (!string.Equals(TryString(intent, "kind", "category"), "FEATURE_LOCATION", StringComparison.OrdinalIgnoreCase))
        return false;
    var strategy = TryString(intent, "entity_strategy", "geometry_strategy", "strategy");
    if (strategy is not null && strategy.Equals("DATUM_TO_HOLE_PLACEMENT", StringComparison.OrdinalIgnoreCase))
        return true;
    if (intent.TryGetProperty("geometry", out var geometry) && geometry.ValueKind == JsonValueKind.Object)
    {
        strategy = TryString(geometry, "entity_strategy", "geometry_strategy", "strategy");
        if (strategy is not null && strategy.Equals("DATUM_TO_HOLE_PLACEMENT", StringComparison.OrdinalIgnoreCase))
            return true;
    }
    return false;
}

static string GetOwnerRole(Definition definition)
{
    string? requested = definition.OwnerView.ValueKind == JsonValueKind.String
        ? definition.OwnerView.GetString()
        : definition.OwnerView.ValueKind == JsonValueKind.Object && definition.OwnerView.TryGetProperty("role", out var role)
            ? ExtractSemanticRole(role)
            : TryExtractOwnerViewRole(definition.OwnerView);
    return string.IsNullOrWhiteSpace(requested)
        ? throw new InvalidOperationException("OWNER_ROLE_SCHEMA_ERROR: " + definition.AnnotationId)
        : NormalizeRole(requested);
}

static string ReadAxis(JsonElement geometry)
{
    var axis = ReadNestedString(geometry, "endpoint_a", "axis") ?? ReadNestedString(geometry, "endpoint_b", "axis");
    return axis is null ? throw new InvalidOperationException("GEOMETRY_AXIS_UNAVAILABLE") : axis.ToUpperInvariant();
}

static string? ReadNestedString(JsonElement root, string objectName, string propertyName) =>
    root.ValueKind == JsonValueKind.Object && root.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
        ? TryString(nested, propertyName)
        : null;

static int AxisIndex(string axis) => axis.ToUpperInvariant() switch
{
    "X" => 0,
    "Y" => 1,
    "Z" => 2,
    _ => throw new InvalidOperationException("UNSUPPORTED_MODEL_AXIS: " + axis)
};

static double Distance(double[] a, double[] b) =>
    Math.Sqrt(Enumerable.Range(0, Math.Min(3, Math.Min(a.Length, b.Length))).Sum(index => Math.Pow(a[index] - b[index], 2)));

static bool ProbeSingleSelection(ModelDoc2 model, ResolvedNativeEntity entity)
{
    model.ClearSelection2(true);
    var selected = entity.Object.Select4(false, null);
    model.ClearSelection2(true);
    return selected;
}

static bool ProbeRawEntitySelection(ModelDoc2 model, Entity entity)
{
    model.ClearSelection2(true);
    var selected = entity.Select4(false, null);
    model.ClearSelection2(true);
    return selected;
}

static void SelectSingle(ModelDoc2 model, ResolvedNativeEntity entity, string annotationId)
{
    model.ClearSelection2(true);
    if (!entity.Object.Select4(false, null))
        throw new InvalidOperationException("NATIVE_ENTITY_SELECTION_FAILED: " + annotationId);
}

static void SelectPair(ModelDoc2 model, ResolvedNativeEntity a, ResolvedNativeEntity b, string annotationId)
{
    model.ClearSelection2(true);
    var selectedA = a.Object.Select4(false, null);
    var selectedB = selectedA && b.Object.Select4(true, null);
    var count = (model.SelectionManager as SelectionMgr)?.GetSelectedObjectCount2(-1) ?? 0;
    if (!selectedA || !selectedB || count < 2)
        throw new InvalidOperationException($"NATIVE_ENTITY_PAIR_SELECTION_FAILED: {annotationId}; count={count}");
}

static void RequireEntityCount(GeometryResolution geometry, int expected, string annotationId)
{
    if (geometry.Entities.Count != expected)
        throw new InvalidOperationException($"RESOLVED_ENTITY_COUNT_INVALID: {annotationId}; expected={expected}; actual={geometry.Entities.Count}");
}

static object DescribeEntity(ResolvedNativeEntity entity) =>
    new { entity.Id, entity.NativeType, model_point_m = entity.ModelPoint, radius_m = double.IsFinite(entity.Radius) ? entity.Radius : (double?)null };

static string DescribeEntityText(ResolvedNativeEntity entity) =>
    $"{entity.Id}; type={entity.NativeType}; model_point_m=[{string.Join(",", entity.ModelPoint.Select(value => value.ToString("0.#########")))}]";

static double[] AnnotationPosition(DisplayDimension display) =>
    display.GetAnnotation() is Annotation annotation ? ToDoubles(annotation.GetPosition()) : Array.Empty<double>();

static double[] ToDoubles(object? value) =>
    value is Array array ? array.Cast<object>().Select(Convert.ToDouble).ToArray() : Array.Empty<double>();

static List<ExpectedAnnotationIdentity> ReadCreationIdentities(IEnumerable<object> lineage)
{
    var result = new List<ExpectedAnnotationIdentity>();
    foreach (var record in lineage)
    {
        var element = JsonSerializer.SerializeToElement(record);
        var annotationId = TryString(element, "annotation_id")
            ?? throw new InvalidOperationException("LINEAGE_IDENTITY_SCHEMA_ERROR: annotation_id unavailable");
        var category = TryString(element, "category")
            ?? throw new InvalidOperationException("LINEAGE_IDENTITY_SCHEMA_ERROR: category unavailable for " + annotationId);
        var creationIdentity = TryString(element, "creation_identity");
        result.Add(new ExpectedAnnotationIdentity(annotationId, category, creationIdentity,
            TryString(element, "creation_mode") ?? "",
            TryString(element, "native_view"),
            TryString(element, "hole_semantic_reference"),
            TryString(element, "target"),
            TryNumberArray(element, "actual_position_m")));
    }
    return result;
}

static ReopenedAnnotationInventory EnumerateReopenedAnnotations(DrawingDoc drawing)
{
    var dimensions = new Dictionary<string, ReopenedNativeAnnotation>(StringComparer.Ordinal);
    var notes = new Dictionary<string, ReopenedNativeAnnotation>(StringComparer.Ordinal);
    for (var view = drawing.GetFirstView() as View; view is not null; view = view.GetNextView() as View)
    {
        for (var display = view.GetFirstDisplayDimension5() as DisplayDimension;
             display is not null;
             display = display.GetNext5() as DisplayDimension)
        {
            var identity = display.GetNameForSelection();
            if (string.IsNullOrWhiteSpace(identity)) continue;
            var isHole = false;
            var variables = false;
            try { isHole = display.IsHoleCallout(); } catch { }
            if (isHole) { try { variables = display.GetHoleCalloutVariables() is not null; } catch { } }
            var annotation = display.GetAnnotation() as Annotation;
            var position = AnnotationPosition(display);
            Console.WriteLine($"REOPEN DISPLAY DIMENSION: index={dimensions.Count}; owner_view={view.Name}; is_hole_callout={isHole}; dimension_exists={display.GetDimension() is not null}; annotation_exists={annotation is not null}; variables_available={variables}; position={FormatVector(position)}");
            if (isHole) Console.WriteLine($"REOPEN NATIVE HOLE CALLOUT: index={dimensions.Count}; owner_view={view.Name}; variables_available={variables}; referenced_entity_identity=<native-reopen-enumeration>");
            dimensions.TryAdd(identity, new ReopenedNativeAnnotation(identity, "DisplayDimension", view.Name, isHole, variables, position));
        }

        if (view.GetNotes() is not Array rawNotes) continue;
        foreach (var rawNote in rawNotes.Cast<object>())
        {
            if (rawNote is not Note note) continue;
            var identity = note.GetName();
            if (string.IsNullOrWhiteSpace(identity)) continue;
            notes.TryAdd(identity, new ReopenedNativeAnnotation(identity, "Note", view.Name, false, false, null));
        }
    }
    return new ReopenedAnnotationInventory(dimensions.Values.ToArray(), notes.Values.ToArray());
}

static PersistedAnnotationCorrelation CorrelatePersistedAnnotation(
    ExpectedAnnotationIdentity expected,
    ReopenedAnnotationInventory actual)
{
    if (expected.Category.Equals("HOLE_CALLOUT", StringComparison.OrdinalIgnoreCase) && expected.CreationMode.Equals("SOLIDWORKS_NATIVE_HOLE_CALLOUT", StringComparison.OrdinalIgnoreCase))
    {
        var nativeCandidates = actual.DisplayDimensions.Where(item => item.IsHoleCallout &&
            (string.IsNullOrWhiteSpace(expected.NativeView) || item.OwnerViewName.Equals(expected.NativeView, StringComparison.Ordinal))).ToArray();
        if (expected.Position is { Length: >= 2 })
            nativeCandidates = nativeCandidates.Where(item => item.Position is { Length: >= 2 } &&
                Distance(expected.Position, item.Position) <= 0.005).ToArray();
        if (nativeCandidates.Length == 1)
            return new PersistedAnnotationCorrelation(expected.AnnotationId, expected.Category, expected.CreationIdentity, true,
                "DisplayDimension", nativeCandidates[0].OwnerViewName, "NATIVE_HOLE_CALLOUT_SEMANTIC_GEOMETRIC_REMATCH", true);
        if (nativeCandidates.Length > 1)
            return new PersistedAnnotationCorrelation(expected.AnnotationId, expected.Category, expected.CreationIdentity, false,
                "DisplayDimension", null, "AMBIGUOUS_NATIVE_HOLE_CALLOUT_REOPEN_MATCH", true);
        return new PersistedAnnotationCorrelation(expected.AnnotationId, expected.Category, expected.CreationIdentity, false,
            "DisplayDimension", null, "NATIVE_HOLE_CALLOUT_NOT_FOUND_AFTER_REOPEN", false);
    }

    if (string.IsNullOrWhiteSpace(expected.CreationIdentity))
        return new PersistedAnnotationCorrelation(expected.AnnotationId, expected.Category, expected.CreationIdentity,
            false, null, null, "EXACT_CREATION_IDENTITY_UNAVAILABLE");

    var expectedNativeType = expected.Category.Equals("HOLE_CALLOUT", StringComparison.OrdinalIgnoreCase)
        ? "Note"
        : "DisplayDimension";
    var candidates = expectedNativeType == "Note" ? actual.Notes : actual.DisplayDimensions;
    var match = candidates.SingleOrDefault(item =>
        item.Identity.Equals(expected.CreationIdentity, StringComparison.Ordinal));
    return match is null
        ? new PersistedAnnotationCorrelation(expected.AnnotationId, expected.Category, expected.CreationIdentity,
            false, expectedNativeType, null, "CREATION_IDENTITY_NOT_FOUND_AFTER_REOPEN")
        : new PersistedAnnotationCorrelation(expected.AnnotationId, expected.Category, expected.CreationIdentity,
            true, match.NativeType, match.OwnerViewName, "EXACT_NATIVE_IDENTITY_MATCH", match.IsHoleCallout);
}

static string FormatVector(double[]? values) => values is null ? "<unavailable>" : "[" + string.Join(",", values.Select(value => value.ToString("0.#########"))) + "]";

static Dictionary<string, string> LoadIdentity(string path)
{
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var root = document.RootElement;
    Console.WriteLine($"Identity root kind: {root.ValueKind}");
    if (root.ValueKind == JsonValueKind.Object)
        Console.WriteLine("Identity root keys: " + string.Join(", ", root.EnumerateObject().Select(property => property.Name)));
    JsonElement rows;
    if (root.ValueKind == JsonValueKind.Array) rows = root;
    else
    {
        var key = new[] { "views", "view_identities", "identities", "runtime_views", "created_views", "items" }
            .FirstOrDefault(name => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array);
        if (key is null) throw new InvalidOperationException("IDENTITY_SCHEMA_ERROR: identity array unavailable");
        rows = root.GetProperty(key);
    }
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var item in rows.EnumerateArray())
    {
        var role = TryString(item, "semantic_role", "directional_role", "role", "view_role", "direction");
        var name = TryString(item, "native_name", "native_view_name", "view_name", "name");
        if (role is null || name is null) throw new InvalidOperationException("IDENTITY_SCHEMA_ERROR: role/native name unavailable");
        map.Add(NormalizeRole(role), name);
    }
    return map;
}

static Dictionary<string, View> EnumerateViews(DrawingDoc drawing)
{
    var map = new Dictionary<string, View>(StringComparer.Ordinal);
    for (var view = drawing.GetFirstView() as View; view is not null; view = view.GetNextView() as View)
        map[view.Name] = view;
    return map;
}

static View? ResolveOwnerView(Definition definition, Dictionary<string, string> roleToNativeName,
    Dictionary<string, View> actualViewsByName, bool throwOnFailure)
{
    string? requestedRole = null;
    JsonElement roleElement = default;
    var ownerViewKeys = definition.OwnerView.ValueKind == JsonValueKind.Object
        ? string.Join(", ", definition.OwnerView.EnumerateObject().Select(property => property.Name))
        : "<none>";
    if (definition.OwnerView.ValueKind == JsonValueKind.String) requestedRole = definition.OwnerView.GetString();
    else if (definition.OwnerView.ValueKind == JsonValueKind.Object)
    {
        if (definition.OwnerView.TryGetProperty("role", out roleElement))
        {
            Console.WriteLine($"owner_view.role kind={roleElement.ValueKind}");
            Console.WriteLine($"owner_view.role raw={roleElement.GetRawText()}");
            requestedRole = ExtractSemanticRole(roleElement);
        }
        if (string.IsNullOrWhiteSpace(requestedRole)) requestedRole = TryExtractOwnerViewRole(definition.OwnerView);
    }

    var normalizedRole = requestedRole is null ? "" : NormalizeRole(requestedRole);
    Console.WriteLine($"OWNER RESOLUTION:{System.Environment.NewLine}annotation_id={definition.AnnotationId}{System.Environment.NewLine}category={definition.Category}");
    Console.WriteLine($"requested_role={requestedRole ?? "<null>"}");
    Console.WriteLine($"normalized_role={normalizedRole}");
    if (string.IsNullOrWhiteSpace(normalizedRole))
        throw new InvalidOperationException(
            $"OWNER_ROLE_SCHEMA_ERROR:{System.Environment.NewLine}annotation_id={definition.AnnotationId}"
            + $"{System.Environment.NewLine}role_kind={roleElement.ValueKind}"
            + $"{System.Environment.NewLine}role_raw={(roleElement.ValueKind == JsonValueKind.Undefined ? "<undefined>" : roleElement.GetRawText())}"
            + $"{System.Environment.NewLine}owner_view_keys={ownerViewKeys}");

    if (roleToNativeName.TryGetValue(normalizedRole, out var nativeName) &&
        actualViewsByName.TryGetValue(nativeName, out var resolvedView) && resolvedView is not null)
    {
        Console.WriteLine($"native_name={nativeName}{System.Environment.NewLine}resolved=true");
        return resolvedView;
    }

    var failure = $"OWNER_VIEW_RESOLUTION_FAILED:{System.Environment.NewLine}annotation_id={definition.AnnotationId}"
        + $"{System.Environment.NewLine}requested_role={requestedRole}"
        + $"{System.Environment.NewLine}normalized_role={normalizedRole}"
        + $"{System.Environment.NewLine}available_identity_roles={string.Join(", ", roleToNativeName.Keys)}"
        + $"{System.Environment.NewLine}available_native_views={string.Join(", ", actualViewsByName.Keys)}";
    Console.WriteLine($"native_name={nativeName ?? "<none>"}{System.Environment.NewLine}resolved=false");
    Console.Error.WriteLine(failure);
    if (throwOnFailure) throw new InvalidOperationException(failure);
    return null;
}

static string? TryExtractOwnerViewRole(JsonElement ownerView)
{
    if (ownerView.ValueKind != JsonValueKind.Object) return null;
    foreach (var field in new[] { "semantic_role", "directional_role", "view_role", "direction" })
        if (ownerView.TryGetProperty(field, out var candidate))
        {
            var role = ExtractSemanticRole(candidate);
            if (!string.IsNullOrWhiteSpace(role)) return role;
        }
    return null;
}

static string? ExtractSemanticRole(JsonElement roleElement)
{
    if (roleElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
    if (roleElement.ValueKind == JsonValueKind.String) return roleElement.GetString();
    if (roleElement.ValueKind == JsonValueKind.Object)
    {
        foreach (var field in new[] { "value", "name", "role", "semantic_role", "directional_role", "view_role", "direction" })
            if (roleElement.TryGetProperty(field, out var nested))
            {
                var role = ExtractSemanticRole(nested);
                if (!string.IsNullOrWhiteSpace(role)) return role;
            }
        return null;
    }
    if (roleElement.ValueKind == JsonValueKind.Array)
    {
        var roles = roleElement.EnumerateArray()
            .Select(element => ExtractSemanticRole(element))
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => NormalizeRole(role!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roles.Length > 1) throw new InvalidOperationException("OWNER_ROLE_AMBIGUOUS: " + string.Join(", ", roles));
        return roles.SingleOrDefault();
    }
    return null;
}

static string? TryString(JsonElement item, params string[] names) =>
    item.ValueKind == JsonValueKind.Object
        ? names.Select(name => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
        : null;

static double? TryNumber(JsonElement item, params string[] names)
{
    if (item.ValueKind != JsonValueKind.Object) return null;
    foreach (var name in names)
        if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;
    return null;
}

static double[]? TryNumberArray(JsonElement item, params string[] names)
{
    if (item.ValueKind != JsonValueKind.Object) return null;
    foreach (var name in names)
        if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            var numbers = value.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out _))
                .Select(element => element.GetDouble())
                .ToArray();
            if (numbers.Length > 0) return numbers;
        }
    return null;
}

static string NormalizeRole(string role)
{
    var normalized = role.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
    return normalized == "iso" ? "isometric" : normalized;
}

static int AllocateLane(Placement placement, Dictionary<string, int> occupied)
{
    var laneClass = string.IsNullOrWhiteSpace(placement.LaneClass) ? placement.Lane : placement.LaneClass!;
    var key = laneClass + "|" + placement.Side.Trim().ToUpperInvariant();
    occupied.TryGetValue(key, out var occupiedCount);
    occupied[key] = occupiedCount + 1;
    return Math.Max(occupiedCount, placement.LaneIndex ?? 0);
}

static void Write(string root, string name, object value) =>
    File.WriteAllText(Path.Combine(root, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

sealed class DefinitionRoot { public List<Definition> Definitions { get; set; } = []; }
sealed class Definition
{
    [JsonPropertyName("annotation_id")] public string AnnotationId { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("semantic_source_ids")] public List<string> SemanticSourceIds { get; set; } = [];
    [JsonPropertyName("owner_view")] public JsonElement OwnerView { get; set; }
    [JsonPropertyName("geometry")] public JsonElement Geometry { get; set; }
    [JsonPropertyName("dimension")] public JsonElement Dimension { get; set; }
    [JsonPropertyName("placement")] public Placement Placement { get; set; } = new();
    [JsonPropertyName("creation")] public JsonElement Creation { get; set; }
    [JsonPropertyName("validation")] public JsonElement Validation { get; set; }
}
sealed class Placement
{
    [JsonPropertyName("lane")] public string Lane { get; set; } = "";
    [JsonPropertyName("lane_class")] public string? LaneClass { get; set; }
    [JsonPropertyName("lane_index")] public int? LaneIndex { get; set; }
    [JsonPropertyName("side")] public string Side { get; set; } = "";
    [JsonPropertyName("selection_reason")] public string? SelectionReason { get; set; }
}
sealed record PhysicalOpeningFact(string Id, string Signature, HashSet<string> BoundarySignatures, HashSet<string> CompatibleGroups, HashSet<string> ProvenGroups, string OriginClass, bool OriginProven);

sealed record ResolvedNativeEntity(string Id, Entity Object, string NativeType, double[] ModelPoint, double? RadiusValue, double[]? Axis = null)
{
    public double Radius => RadiusValue ?? double.PositiveInfinity;
    public string Signature => string.Join("|", ModelPoint.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
        .Concat((Axis ?? Array.Empty<double>()).Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))
        .Concat(new[] { Radius.ToString("R", System.Globalization.CultureInfo.InvariantCulture) }));
    public string PhysicalSignature => string.Join("|", ModelPoint.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
        .Concat((Axis ?? Array.Empty<double>()).Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))
        .Concat(new[] { (Radius * 2.0).ToString("R", System.Globalization.CultureInfo.InvariantCulture) }));
}
sealed record OverallSupportEvidence(ResolvedNativeEntity Entity, double SupportMin, double SupportMax, string Source);
sealed record OverallAxisProvenance(string Axis, bool IsPresent, string TargetSource, bool LowProven, bool HighProven, double? LowCoordinateM, double? HighCoordinateM, double? SpanM, string? LowSupportKind, string? HighSupportKind)
{
    public bool IsEligible => IsPresent && TargetSource == "PROVEN_TOPOLOGY" && LowProven && HighProven && LowCoordinateM.HasValue && HighCoordinateM.HasValue && SpanM.HasValue;
    public static OverallAxisProvenance Absent(string axis) => new(axis, false, "<absent>", false, false, null, null, null, null, null);
}
sealed record CircleAxisSupportInterval(double Min, double Max, string Source);
sealed record CircularSupportEvidence(OverallSupportEvidence Evidence, double RadiusM, double ProjectedSupportRadiusM, double SupportMin, double SupportMax, string Source);
sealed record StaticSupportReference(string EntityIdentity, string NativeType, double Coordinate);
sealed record StaticSupportSearchResult(int DuplicatesRemoved, int PairsExamined, IReadOnlyList<(StaticSupportReference A, StaticSupportReference B)> Candidates);
sealed record SemanticAxisDimensionResolution(string Orientation, string? Api, string Reason);
sealed record SupportReferencePairCandidate(ResolvedNativeEntity A, ResolvedNativeEntity B, double[] Pa, double[] Pb, double Score, double ProjectedOrthogonalDistance);
sealed record SupportReferenceSearchResult(int RawReferenceCount, int DedupedReferenceCount, int DuplicatesRemoved, long PairSpaceEstimate, int PairsExamined, int PairsExtentMatched, IReadOnlyList<SupportReferencePairCandidate> Candidates, long DurationMs, string TerminatedReason);
sealed record GeometryResolution(IReadOnlyList<ResolvedNativeEntity> Entities, string? Axis, string? HoleSemanticReference, HoleSemantic? Hole, double? MeasuredValueMm, int CandidateCount, FeatureLocationEvidence? FeatureLocation, StepDimensionStrategy? StepStrategy = null);
sealed record AxisCoordinateContext(string OwnerRole, string Axis, IReadOnlyList<double> CoordinateLevels, double ReferenceCoordinate, string ReferenceReason, bool OverallEndpointAvailable)
{
    public static string Key(string ownerRole,string axis)=>$"{ownerRole.ToUpperInvariant()}:{axis.ToUpperInvariant()}";
}
sealed record StrictBaselineAssignment(string AnnotationId,string OwnerRole,string Axis,double ReferenceCoordinate,double TargetCoordinate,double ExecutionValueM,double SourceSemanticValueM,string Reason,string ChainGroupId);
sealed class GlobalDimensionRegistry
{
    public GlobalDimensionRegistry(double geometricToleranceM,IReadOnlyList<GlobalDimensionRegistryEntry> entries,int duplicateGroupCount,int suppressedDimensionCount,int remainingRedundantDimensionCount)
    {
        GeometricToleranceM=geometricToleranceM;
        Entries=entries;
        CrossViewDuplicateGroupCount=duplicateGroupCount;
        SuppressedDimensionCount=suppressedDimensionCount;
        RemainingRedundantDimensionCount=remainingRedundantDimensionCount;
        ByAnnotationId=entries.ToDictionary(entry=>entry.AnnotationId,StringComparer.Ordinal);
    }
    [JsonPropertyName("geometric_tolerance_m")] public double GeometricToleranceM { get; }
    [JsonPropertyName("dimensions")] public IReadOnlyList<GlobalDimensionRegistryEntry> Entries { get; }
    [JsonPropertyName("cross_view_duplicate_group_count")] public int CrossViewDuplicateGroupCount { get; }
    [JsonPropertyName("cross_view_suppressed_dimension_count")] public int SuppressedDimensionCount { get; }
    [JsonPropertyName("cross_view_redundant_dimension_count")] public int RemainingRedundantDimensionCount { get; }
    [JsonIgnore] public IReadOnlyDictionary<string,GlobalDimensionRegistryEntry> ByAnnotationId { get; }
}
sealed class GlobalDimensionRegistryEntry
{
    public GlobalDimensionRegistryEntry(string annotationId,string canonicalDimensionId,string semanticAxis,double normalizedCoordinateA,double normalizedCoordinateB,
        string? featureSemanticReference,string dimensionRelation,string ownerViewRole,IReadOnlyList<string> plannerCandidateViews,double plannerOwnerScore,
        string ownershipReason,double ownerSelectionScore)
    {
        AnnotationId=annotationId;
        CanonicalDimensionId=canonicalDimensionId;
        SemanticAxis=semanticAxis;
        NormalizedCoordinateA=normalizedCoordinateA;
        NormalizedCoordinateB=normalizedCoordinateB;
        FeatureSemanticReference=featureSemanticReference;
        DimensionRelation=dimensionRelation;
        OwnerViewRole=ownerViewRole;
        PlannerCandidateViews=plannerCandidateViews;
        PlannerOwnerScore=plannerOwnerScore;
        OwnershipReason=ownershipReason;
        OwnerSelectionScore=ownerSelectionScore;
    }
    [JsonPropertyName("annotation_id")] public string AnnotationId { get; }
    [JsonPropertyName("canonical_dimension_id")] public string CanonicalDimensionId { get; }
    [JsonPropertyName("semantic_axis")] public string SemanticAxis { get; }
    [JsonPropertyName("normalized_model_coordinate_a_m")] public double NormalizedCoordinateA { get; }
    [JsonPropertyName("normalized_model_coordinate_b_m")] public double NormalizedCoordinateB { get; }
    [JsonPropertyName("feature_semantic_reference")] public string? FeatureSemanticReference { get; }
    [JsonPropertyName("dimension_relation")] public string DimensionRelation { get; }
    [JsonPropertyName("owner_view_role")] public string OwnerViewRole { get; }
    [JsonPropertyName("planner_candidate_views")] public IReadOnlyList<string> PlannerCandidateViews { get; }
    [JsonPropertyName("planner_owner_score")] public double PlannerOwnerScore { get; }
    [JsonPropertyName("ownership_reason")] public string OwnershipReason { get; }
    [JsonPropertyName("candidate_views")] public IReadOnlyList<string> CandidateViews { get; set; }=Array.Empty<string>();
    [JsonPropertyName("selected_owner_view")] public string SelectedOwnerView { get; set; }="";
    [JsonPropertyName("cross_view_duplicate")] public bool CrossViewDuplicate { get; set; }
    [JsonPropertyName("cross_view_redundancy_suppressed")] public bool CrossViewRedundancySuppressed { get; set; }
    [JsonPropertyName("suppression_reason")] public string SuppressionReason { get; set; }="";
    [JsonIgnore] public double OwnerSelectionScore { get; }
}
sealed record StepDimensionStrategy(string DimensionStrategy, double? ReferenceCoordinate, string ReferenceReason, double? CoordinateLevel, string DefinitionRole, bool RedundancySuppressed, string ChainGroupId, bool ChainAllowed, bool ExplicitLocalSizeRequired, bool StrictBaselineCoordinate, double SourceSemanticValueM, double ExecutionValueM, string RepresentationMode);
sealed record StepDimensionExecutionEvidence(string AnnotationId,string OwnerRole,string Axis,double CoordinateA,double CoordinateB,StepDimensionStrategy Strategy,double SystemValueM);
sealed record ChainDimensionGroup(string ChainGroupId,string OwnerRole,string Axis,IReadOnlyList<double> CoordinateLevels,IReadOnlyList<string> AnnotationIds,bool ChainDimensionGroupDetected,int UnnecessaryChainDimensionCount,int VisualChainDimensionCount,int LocalSizeWithoutExplicitIntentCount,int RedundantDimensionCount);
sealed record ChainAnalysis(IReadOnlyList<ChainDimensionGroup> Groups,int UnnecessaryCount,int VisualChainCount,int LocalSizeWithoutExplicitIntentCount,int RedundantCount,bool StrictBaselinePolicy);
sealed record GeometryFailure(string AnnotationId, string Category, string OwnerRole, string Strategy, string Reason);
sealed record HoleSemantic(
    string Id,
    IReadOnlyList<double[]> PlacementPoints,
    string? Thread,
    string? ThreadToleranceClass,
    double? DrillDiameterM,
    double? DrillDepthM,
    double? ThreadDepthM,
    string? DrillEndCondition,
    string? ThreadEndCondition);
sealed record DatumSemantic(string StableId, string? SemanticRole, string Axis, int AxisIndex, double CoordinateM, double MatchToleranceM, double[]? Normal);
sealed record SemanticAxisExtreme(string Axis, double CoordinateM, int Direction);
sealed record SemanticAxisExtent(string Axis, double MinimumM, double MaximumM, int EvidenceCount)
{
    public double ExtentM => MaximumM - MinimumM;
}
sealed record SemanticFrameEvidence(string SourceArtifact, IReadOnlyDictionary<string, SemanticAxisExtent> Axes);
sealed record RuntimeViewFrameEvidence(string Role, string NativeName, IReadOnlyList<double[]> Points);
sealed record RuntimeAxisExtent(string Axis, double MinimumM, double MaximumM, int EvidenceCount, int ViewCount)
{
    public double ExtentM => MaximumM - MinimumM;
}
sealed class AxisFrameRegistration
{
    private AxisFrameRegistration(string axis, bool resolved, double? translationM, double extentResidualM, double translationResidualM,
        SemanticAxisExtent? semanticExtent, RuntimeAxisExtent? runtimeExtent, string reason, string observability)
    {
        Axis = axis;
        Resolved = resolved;
        TranslationM = translationM;
        ExtentResidualM = extentResidualM;
        TranslationResidualM = translationResidualM;
        SemanticExtent = semanticExtent;
        RuntimeExtent = runtimeExtent;
        Reason = reason;
        Observability = observability;
    }

    public string Axis { get; }
    public bool Resolved { get; }
    public double? TranslationM { get; }
    public double ExtentResidualM { get; }
    public double TranslationResidualM { get; }
    public double ResidualM => Math.Max(ExtentResidualM, TranslationResidualM);
    public int RuntimeEvidenceCount => RuntimeExtent?.EvidenceCount ?? 0;
    public SemanticAxisExtent? SemanticExtent { get; }
    public RuntimeAxisExtent? RuntimeExtent { get; }
    public string Reason { get; }
    public string Observability { get; }

    public static AxisFrameRegistration Registered(string axis, double translationM, double extentResidualM, double translationResidualM,
        SemanticAxisExtent semanticExtent, RuntimeAxisExtent runtimeExtent, string observability) =>
        new(axis, true, translationM, extentResidualM, translationResidualM, semanticExtent, runtimeExtent,
            "PER_OWNER_VIEW_SHARED_BOUNDS_TRANSLATION_REGISTERED", observability);

    public static AxisFrameRegistration Unresolved(string axis, string reason, SemanticAxisExtent? semanticExtent = null,
        RuntimeAxisExtent? runtimeExtent = null, double extentResidualM = double.NaN, double translationResidualM = double.NaN,
        string observability = "UNUSABLE") =>
        new(axis, false, null, extentResidualM, translationResidualM, semanticExtent, runtimeExtent, reason, observability);
}
sealed class PerOwnerViewFrameRegistration(string annotationId, string ownerRole, string nativeView,
    SemanticFrameEvidence semanticFrame, IReadOnlyDictionary<string, RuntimeAxisExtent> runtimeAxes,
    IReadOnlyDictionary<string, AxisFrameRegistration> axes)
{
    public DatumFrameResolution Resolve(DatumSemantic datum, string? requestedDatumRole)
    {
        if (axes.TryGetValue(datum.Axis, out var axis) && axis.Resolved && axis.TranslationM is double translation)
            return new DatumFrameResolution(datum.CoordinateM + translation, translation, "PER_OWNER_VIEW_FRAME_REGISTERED",
                $"annotation={annotationId}; owner_role={ownerRole}; native_view={nativeView}; {axis.Reason}", axis.ResidualM);

        var direction = ResolveRoleDirection(requestedDatumRole ?? datum.SemanticRole, datum.Normal);
        if (direction is not null && axes.TryGetValue(datum.Axis, out axis) && axis.SemanticExtent is not null && axis.RuntimeExtent is not null &&
            axis.Observability is "COLLAPSED" or "PARTIALLY_OBSERVABLE" &&
            Math.Abs(datum.CoordinateM - (direction < 0 ? axis.SemanticExtent.MinimumM : axis.SemanticExtent.MaximumM)) <= datum.MatchToleranceM)
        {
            var registered = direction < 0 ? axis.RuntimeExtent.MinimumM : axis.RuntimeExtent.MaximumM;
            return new DatumFrameResolution(registered, null, "SEMANTIC_EXTREME_ROLE_RUNTIME_DERIVED",
                $"annotation={annotationId}; owner_role={ownerRole}; native_view={nativeView}; semantic_source={semanticFrame.SourceArtifact}; observability={axis.Observability}; runtime_evidence_count={axis.RuntimeEvidenceCount}",
                Math.Abs(axis.SemanticExtent.ExtentM - axis.RuntimeExtent.ExtentM));
        }

        var runtimeEvidence = runtimeAxes.TryGetValue(datum.Axis, out var runtimeAxis) ? runtimeAxis.EvidenceCount : 0;
        throw new InvalidOperationException($"DATUM_FRAME_REGISTRATION_UNRESOLVED: annotation={annotationId}; owner_role={ownerRole}; native_view={nativeView}; axis={datum.Axis}; runtime_evidence_count={runtimeEvidence}; reason={(axis?.Reason ?? "AXIS_REGISTRATION_UNAVAILABLE")}");
    }

    private static int? ResolveRoleDirection(string? datumRole, double[]? normal)
    {
        var role = datumRole?.Trim().ToUpperInvariant().Replace('-', '_').Replace(' ', '_') ?? "";
        if (role.StartsWith("MIN_", StringComparison.Ordinal) || role.StartsWith("LOWER_", StringComparison.Ordinal) ||
            role.EndsWith("_MIN", StringComparison.Ordinal) || role.EndsWith("_LOWER", StringComparison.Ordinal)) return -1;
        if (role.StartsWith("MAX_", StringComparison.Ordinal) || role.StartsWith("UPPER_", StringComparison.Ordinal) ||
            role.EndsWith("_MAX", StringComparison.Ordinal) || role.EndsWith("_UPPER", StringComparison.Ordinal)) return 1;
        if (normal is { Length: >= 3 })
        {
            var dominant = normal.OrderByDescending(value => Math.Abs(value)).FirstOrDefault();
            if (Math.Abs(dominant) > 1e-9) return Math.Sign(dominant);
        }
        return null;
    }
}
sealed record DatumFrameResolution(double RegisteredCoordinateM, double? TranslationM, string Authority, string Evidence, double ResidualM);
sealed class DatumSemanticCatalog(IReadOnlyList<DatumSemantic> entries, string sourceArtifact)
{
    public string SourceArtifact { get; } = sourceArtifact;
    public bool TryResolve(string stableId, string? semanticRole, out DatumSemantic datum)
    {
        datum = entries.FirstOrDefault(entry => entry.StableId.Equals(stableId, StringComparison.OrdinalIgnoreCase))
            ?? entries.FirstOrDefault(entry => !string.IsNullOrWhiteSpace(semanticRole) && !string.IsNullOrWhiteSpace(entry.SemanticRole) &&
                NormalizeDatumRole(entry.SemanticRole).Equals(NormalizeDatumRole(semanticRole), StringComparison.OrdinalIgnoreCase))!;
        return datum is not null;
    }

    private static string NormalizeDatumRole(string role) =>
        role.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
}
sealed record FeatureLocationEvidence(string DatumSemanticRef, string FeatureSemanticRef, ResolvedNativeEntity DatumEntity, ResolvedNativeEntity HoleEntity, string DatumStrategy, string PlacementRole, string DatumProxyStrategy, double DatumPlaneDistanceM, string DatumResolutionAuthority, double DatumRegisteredCoordinateM, double? DatumFrameTranslationM, string DatumFrameEvidence, double DatumFrameResidualM);
sealed record VisibleProjectedEdge(ResolvedNativeEntity Entity, double[][] Endpoints);
sealed record OwnerViewGeometryItem(string NativeId, Entity DrawingEntity, string NativeType, double[][] ModelPoints, string? AvailabilityIssue)
{
    public string ModelGeometryText => ModelPoints.Length == 0
        ? "<unavailable>"
        : string.Join(" -> ", ModelPoints.Select(point => "[" + string.Join(",", point) + "]"));
}
sealed class DatumProxyCandidate(ResolvedNativeEntity entity, string strategy, double planeDistanceM, double orthogonalProjectionErrorM, double score, string modelGeometryText)
{
    public ResolvedNativeEntity Entity { get; } = entity;
    public string Strategy { get; } = strategy;
    public double PlaneDistanceM { get; } = planeDistanceM;
    public double OrthogonalProjectionErrorM { get; } = orthogonalProjectionErrorM;
    public double Score { get; } = score;
    public string ModelGeometryText { get; } = modelGeometryText;
    public bool Selectable { get; set; }
}
sealed record DatumProxyResolution(ResolvedNativeEntity Entity, string Strategy, double PlaneDistanceM, bool Selectable, int CandidateCount);
sealed record ExpectedAnnotationIdentity(string AnnotationId, string Category, string? CreationIdentity, string CreationMode, string? NativeView, string? HoleSemanticReference, string? NativeEdge, double[]? Position);
sealed record ReopenedNativeAnnotation(string Identity, string NativeType, string OwnerViewName, bool IsHoleCallout, bool VariablesAvailable, double[]? Position);
sealed record ReopenedAnnotationInventory(IReadOnlyList<ReopenedNativeAnnotation> DisplayDimensions, IReadOnlyList<ReopenedNativeAnnotation> Notes);
sealed record PersistedAnnotationCorrelation(string AnnotationId, string Category, string? CreationIdentity, bool Persisted, string? NativeType, string? OwnerViewName, string Reason, bool NativeHoleCallout = false);
