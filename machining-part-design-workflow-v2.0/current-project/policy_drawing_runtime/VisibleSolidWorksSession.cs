using System.Diagnostics;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

// Controlled regression session only. No attach, process termination or COM release fallback.
sealed class VisibleSolidWorksSession : IDisposable
{
    public enum AttachState { ROT_ATTACHED, MANUAL_SESSION_REQUIRED, NO_SOLIDWORKS, MULTIPLE_SOLIDWORKS }
    static SldWorks? retainedRoot;
    readonly Mutex gate;
    readonly bool ownsGate;
    public SldWorks? Root { get; private set; }
    public AttachState Status { get; private set; }
    public bool ManualSessionUsable { get; private set; }
    public bool GuiSessionReady { get; private set; }
    public bool ComSessionReady { get; private set; }
    public string ComRootReason { get; private set; } = "UNKNOWN";
    public long SessionFrameHwnd { get; private set; }
    public string SessionWindowSource { get; private set; } = "NONE";
    public int OwnedPid { get; private set; }
    public DateTime OwnedStartTime { get; private set; }
    VisibleSolidWorksSession(Mutex gate, bool ownsGate=false) { this.gate=gate; this.ownsGate=ownsGate; }

    [DllImport("ole32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]
    static extern int CLSIDFromProgID(string progId,out Guid clsid);
    [DllImport("oleaut32.dll",PreserveSig=false,ExactSpelling=true)]
    static extern void GetActiveObject(ref Guid clsid,nint reserved,[MarshalAs(UnmanagedType.IUnknown)] out object instance);

    public static VisibleSolidWorksSession Attach()
    {
        var gate=new Mutex(false,@"Local\GeneralizedDrawingAttachedSolidWorksSession");bool acquired=false;
        try {
            try {acquired=gate.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}
            var before=Pids();
            Console.WriteLine($"EXECUTOR_PID={System.Environment.ProcessId}\nPREEXISTING_SOLIDWORKS_COUNT={before.Length}\nSOLIDWORKS_PROCESS_COUNT_BEFORE={before.Length}\nPREEXISTING_SOLIDWORKS_PIDS={string.Join(",",before)}\nNEW_SOLIDWORKS_PROCESS_CREATED=false");
            if(before.Length==0) {
                Console.WriteLine("stage=SOLIDWORKS_ATTACH\nATTACH_STATUS=NO_SOLIDWORKS");
                if(acquired) gate.ReleaseMutex();
                gate.Dispose();
                return new VisibleSolidWorksSession(new Mutex(false, @"Local\GeneralizedDrawingAttachedSolidWorksSession")) { Status=AttachState.NO_SOLIDWORKS, ComRootReason="NO_SOLIDWORKS_PROCESS" };
            }
            if(before.Length>1) {
                Console.WriteLine("stage=SOLIDWORKS_ATTACH\nATTACH_STATUS=MULTIPLE_SOLIDWORKS");
                if(acquired) gate.ReleaseMutex();
                gate.Dispose();
                return new VisibleSolidWorksSession(new Mutex(false, @"Local\GeneralizedDrawingAttachedSolidWorksSession")) { Status=AttachState.MULTIPLE_SOLIDWORKS, ComRootReason="MULTIPLE_SOLIDWORKS_PROCESSES" };
            }
            if(!acquired)throw new InvalidOperationException("ATTACHED_EXECUTOR_ALREADY_RUNNING");
            using var process=Process.GetProcessById(before[0]);process.Refresh();
            var start=process.StartTime;
            Console.WriteLine($"EXISTING_PID={process.Id}\nPROCESS_MAIN_WINDOW_HANDLE={process.MainWindowHandle}\nMAIN_WINDOW_TITLE={process.MainWindowTitle}\nRESPONDING={process.Responding}");
            if(!process.Responding) {
                if(acquired) gate.ReleaseMutex();
                gate.Dispose();
                return new VisibleSolidWorksSession(new Mutex(false, @"Local\GeneralizedDrawingAttachedSolidWorksSession")) {
                    Status=AttachState.MANUAL_SESSION_REQUIRED, OwnedPid=process.Id, OwnedStartTime=start,
                    GuiSessionReady=false, ComSessionReady=false, ComRootReason="SOLIDWORKS_NOT_RESPONDING"
                };
            }
            // .NET 10 does not expose Marshal.GetActiveObject; use its native ROT equivalent.
            // CLSIDFromProgID resolves the class; GetActiveObject never activates a new server.
            try {
                Marshal.ThrowExceptionForHR(CLSIDFromProgID("SldWorks.Application",out var clsid));
                GetActiveObject(ref clsid,0,out var instance);
                retainedRoot=(SldWorks)instance;
            }
            catch (COMException ex) {
                var code = unchecked((uint)ex.HResult);
                var rotError = code == 0x800401E3 ? "MK_E_UNAVAILABLE" : $"0x{code:X8}";
                var processHwnd = process.MainWindowHandle;
                Console.WriteLine($"stage=SOLIDWORKS_ATTACH\nROT_ATTACH=FAILED\nATTACH_STATUS=MANUAL_SESSION_REQUIRED\nROT_ERROR={rotError}\nEXISTING_PID={process.Id}\nWINDOW_HANDLE={processHwnd}\nRESPONDING={process.Responding}\nPROCESS_MAIN_WINDOW_HANDLE={processHwnd}");
                Console.WriteLine("COM_FALLBACK_ATTEMPT=true");
                var fallbackBefore = Pids();
                SldWorks? fallbackRoot = null;
                string fallbackResult;
                try
                {
                    // A COM activation request can start a second SLDWORKS server when
                    // the running instance is not registered.  The fallback is therefore
                    // deliberately limited to a non-activating lookup of an already
                    // registered object; process-count validation remains authoritative.
                    Marshal.ThrowExceptionForHR(CLSIDFromProgID("SldWorks.Application",out var fallbackClsid));
                    GetActiveObject(ref fallbackClsid,0,out var fallbackInstance);
                    fallbackRoot = fallbackInstance as SldWorks;
                    var fallbackAfter = Pids();
                    var createdNew = fallbackAfter.Except(fallbackBefore).Any();
                    Console.WriteLine($"SOLIDWORKS_PROCESS_COUNT_BEFORE={fallbackBefore.Length}\nSOLIDWORKS_PROCESS_COUNT_AFTER={fallbackAfter.Length}\nNEW_SOLIDWORKS_PROCESS_CREATED={createdNew.ToString().ToLowerInvariant()}");
                    if (createdNew || fallbackAfter.Length != fallbackBefore.Length || !fallbackAfter.Contains(process.Id))
                    {
                        fallbackResult = "REJECTED_NEW_PROCESS";
                        fallbackRoot = null;
                    }
                    else fallbackResult = fallbackRoot is null ? "FAIL" : "PASS";
                }
                catch (Exception fallbackEx)
                {
                    fallbackResult = $"FAIL:{fallbackEx.GetType().Name}:{fallbackEx.Message}";
                    fallbackRoot = null;
                    Console.WriteLine("SOLIDWORKS_PROCESS_COUNT_BEFORE=" + fallbackBefore.Length + "\nSOLIDWORKS_PROCESS_COUNT_AFTER=" + Pids().Length + "\nNEW_SOLIDWORKS_PROCESS_CREATED=false");
                }
                Console.WriteLine($"COM_FALLBACK_RESULT={fallbackResult}");
                var frameHwnd = TryGetFrameHwnd(fallbackRoot);
                if (fallbackResult == "PASS") retainedRoot = fallbackRoot;
                var source = processHwnd != 0 ? "PROCESS_MAIN_WINDOW_HANDLE" : frameHwnd != 0 ? "IFRAME_HWND" : "NONE";
                var manual = new VisibleSolidWorksSession(gate, acquired) { Root=retainedRoot, Status=AttachState.MANUAL_SESSION_REQUIRED, ManualSessionUsable=(processHwnd != 0 || frameHwnd != 0) && process.Responding, GuiSessionReady=(processHwnd != 0 || frameHwnd != 0) && process.Responding, ComSessionReady=retainedRoot is not null, ComRootReason=retainedRoot is not null ? "COM_FALLBACK_ATTACHED" : "ROT_UNAVAILABLE", SessionFrameHwnd=frameHwnd, SessionWindowSource=source, OwnedPid=process.Id, OwnedStartTime=start };
                Console.WriteLine($"SW_FRAME_AVAILABLE={frameHwnd != 0}\nSW_FRAME_HWND_X64={frameHwnd}\nSESSION_WINDOW_SOURCE={source}\nMANUAL_SESSION_READY={manual.ManualSessionUsable.ToString().ToLowerInvariant()}");
                Console.Out.Flush();
                // The interactive process remains untouched. A COM root is unavailable, so
                // callers can stop at this checkpoint without starting a second server.
                if (manual.ManualSessionUsable) return manual;
                if (acquired) gate.ReleaseMutex();
                gate.Dispose();
                return new VisibleSolidWorksSession(new Mutex(false, @"Local\GeneralizedDrawingAttachedSolidWorksSession")) { Status=AttachState.MANUAL_SESSION_REQUIRED, ManualSessionUsable=false, GuiSessionReady=false, ComSessionReady=false, ComRootReason="ROT_UNAVAILABLE_AND_NO_GUI_HANDLE", OwnedPid=process.Id, OwnedStartTime=start };
            }
            var after=Pids();
            Console.WriteLine($"SOLIDWORKS_PROCESS_COUNT_AFTER_ATTACH={after.Length}");
            if(after.Length!=1||after[0]!=before[0]||retainedRoot.GetProcessID()!=before[0])throw new InvalidOperationException("ROT_PROCESS_IDENTITY_MISMATCH");
            process.Refresh();if(process.StartTime!=start)throw new InvalidOperationException("ROT_PROCESS_REPLACED");
            var session=new VisibleSolidWorksSession(gate, acquired){Root=retainedRoot,OwnedPid=process.Id,OwnedStartTime=start,Status=AttachState.ROT_ATTACHED,ComSessionReady=true,ComRootReason="ROT_ATTACHED"};
            Console.WriteLine("ROT_ATTACH=PASS\nSESSION_MODE=ATTACH_EXISTING\nNEW_SOLIDWORKS_PROCESS_CREATED=false");
            session.RequireGui();return session;
        }catch(Exception e){Console.WriteLine($"ROT_ATTACH=NOT_COMPLETED\nATTACH_FAILURE={e.Message}");if(acquired)gate.ReleaseMutex();gate.Dispose();throw;}
    }

    // Manual-session mode never activates SolidWorks. It can proceed only when
    // an existing process also exposes a registered automation object.
    public static VisibleSolidWorksSession ManualSession()
    {
        var gate=new Mutex(false,@"Local\GeneralizedDrawingManualSession");
        bool acquired=false;
        try { try { acquired=gate.WaitOne(0); } catch(AbandonedMutexException) { acquired=true; } }
        catch { gate.Dispose(); throw; }
        if(!acquired) { gate.Dispose(); throw new InvalidOperationException("MANUAL_SESSION_EXECUTOR_ALREADY_RUNNING"); }
        var pids=Pids();
        Console.WriteLine($"SESSION_MODE=MANUAL_SESSION\nSOLIDWORKS_COUNT={pids.Length}");
        if(pids.Length==0) { gate.ReleaseMutex(); gate.Dispose(); return new VisibleSolidWorksSession(new Mutex(false, @"Local\GeneralizedDrawingManualSession")){Status=AttachState.NO_SOLIDWORKS,ComRootReason="NO_SOLIDWORKS_PROCESS"}; }
        if(pids.Length>1) { gate.ReleaseMutex(); gate.Dispose(); return new VisibleSolidWorksSession(new Mutex(false, @"Local\GeneralizedDrawingManualSession")){Status=AttachState.MULTIPLE_SOLIDWORKS,ComRootReason="MULTIPLE_SOLIDWORKS_PROCESSES"}; }
        using var process=Process.GetProcessById(pids[0]); process.Refresh();
        Console.WriteLine($"EXISTING_PID={process.Id}\nRESPONDING={process.Responding}\nPROCESS_MAIN_WINDOW_HANDLE={process.MainWindowHandle}");
        if(!process.Responding) { gate.ReleaseMutex(); gate.Dispose(); return new VisibleSolidWorksSession(new Mutex(false, @"Local\GeneralizedDrawingManualSession")){Status=AttachState.MANUAL_SESSION_REQUIRED,OwnedPid=process.Id,ComRootReason="SOLIDWORKS_NOT_RESPONDING"}; }
        try
        {
            Marshal.ThrowExceptionForHR(CLSIDFromProgID("SldWorks.Application",out var clsid));
            GetActiveObject(ref clsid,0,out var instance);
            var root=instance as SldWorks ?? throw new COMException("SOLIDWORKS_COM_OBJECT_INVALID");
            var processHwnd=process.MainWindowHandle;
            var frameHwnd=TryGetFrameHwnd(root);
            var guiReady=process.Responding && (processHwnd!=0 || frameHwnd!=0);
            var source=processHwnd!=0?"PROCESS_MAIN_WINDOW_HANDLE":frameHwnd!=0?"IFRAME_HWND":"NONE";
            var s=new VisibleSolidWorksSession(gate,true){Root=root,Status=AttachState.ROT_ATTACHED,OwnedPid=process.Id,OwnedStartTime=process.StartTime,ManualSessionUsable=guiReady,GuiSessionReady=guiReady,ComSessionReady=true,ComRootReason="ROT_ATTACHED",SessionFrameHwnd=frameHwnd,SessionWindowSource=source};
            Console.WriteLine("ROT_ATTACH=PASS\nCOM_FALLBACK_ATTEMPT=false\nNEW_SOLIDWORKS_PROCESS_CREATED=false");
            return s;
        }
        catch(COMException ex)
        {
            var code=unchecked((uint)ex.HResult);var error=code==0x800401E3?"MK_E_UNAVAILABLE":$"0x{code:X8}";
            var hwnd=process.MainWindowHandle;
            var ready=process.Responding && hwnd!=0;
            Console.WriteLine($"ROT_ATTACH=FAILED\nROT_ERROR={error}\nATTACH_STATUS=MANUAL_SESSION_REQUIRED\nCOM_FALLBACK_ATTEMPT=false\nCOM_FALLBACK_RESULT=NOT_ATTEMPTED\nNEW_SOLIDWORKS_PROCESS_CREATED=false\nSW_FRAME_AVAILABLE=false\nSW_FRAME_HWND_X64=0\nSESSION_WINDOW_SOURCE={(ready?"PROCESS_MAIN_WINDOW_HANDLE":"NONE")}\nMANUAL_SESSION_READY={ready.ToString().ToLowerInvariant()}");
            if(ready) return new VisibleSolidWorksSession(gate,true){Status=AttachState.MANUAL_SESSION_REQUIRED,OwnedPid=process.Id,OwnedStartTime=process.StartTime,ManualSessionUsable=true,GuiSessionReady=true,ComSessionReady=false,ComRootReason="ROT_UNAVAILABLE",SessionWindowSource="PROCESS_MAIN_WINDOW_HANDLE"};
            gate.ReleaseMutex(); gate.Dispose();
            return new VisibleSolidWorksSession(new Mutex(false, @"Local\GeneralizedDrawingManualSession")){Status=AttachState.MANUAL_SESSION_REQUIRED,OwnedPid=process.Id,OwnedStartTime=process.StartTime,ManualSessionUsable=false,GuiSessionReady=false,ComSessionReady=false,ComRootReason="ROT_UNAVAILABLE_AND_NO_GUI_HANDLE"};
        }
    }

    static long TryGetFrameHwnd(SldWorks? root)
    {
        try { return root?.Frame() is IFrame frame ? frame.GetHWndx64() : 0; }
        catch { return 0; }
    }

    static int[] Pids()
    {
        var processes=Process.GetProcessesByName("SLDWORKS");
        try { return processes.Select(p=>p.Id).OrderBy(p=>p).ToArray(); }
        finally { foreach(var p in processes)p.Dispose(); }
    }
    public static VisibleSolidWorksSession Start()
    {
        var gate=new Mutex(false,@"Local\GeneralizedDrawingControlledSolidWorksSession");
        bool acquired=false;
        try {
            try {acquired=gate.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}
            var before=Pids();
            Console.WriteLine($"EXECUTOR_PID={System.Environment.ProcessId}\nPREEXISTING_SOLIDWORKS_COUNT={before.Length}\nPREEXISTING_SOLIDWORKS_PIDS={string.Join(",",before)}");
            if(before.Length>0)throw new InvalidOperationException("STALE_SOLIDWORKS_SESSION_DETECTED");
            if(!acquired)throw new InvalidOperationException("CONTROLLED_SESSION_ALREADY_STARTING");
            var session=new VisibleSolidWorksSession(gate, acquired);
            // The one and only COM activation in this controlled runtime.
            var progType=Type.GetTypeFromProgID("SldWorks.Application")
                ?? throw new InvalidOperationException("SldWorks.Application_PROGID_UNAVAILABLE");
            var rawRoot=Activator.CreateInstance(progType)
                ?? throw new InvalidOperationException("SOLIDWORKS_COM_ACTIVATION_RETURNED_NULL");
            retainedRoot=(SldWorks)rawRoot;
            session.Root=retainedRoot;
            session.ComSessionReady=true;
            session.ComRootReason="CONTROLLED_COM_ACTIVATION";
            session.Root.Visible=true;
            try {session.Root.UserControl=true;Console.WriteLine($"SOLIDWORKS_USER_CONTROL={session.Root.UserControl}");}
            catch(Exception e){Console.WriteLine($"SOLIDWORKS_USER_CONTROL_UNAVAILABLE={e.GetType().Name}:{e.Message}");}
            var watch=Stopwatch.StartNew();int[] after;
            do {
                after=Pids();if(after.Length>1)throw new InvalidOperationException("OWNED_SOLIDWORKS_PROCESS_COUNT_NOT_ONE");
                if(after.Length==1)break;
                Thread.Sleep(250);
            }while(watch.Elapsed<TimeSpan.FromSeconds(15));
            if(after.Length!=1)throw new InvalidOperationException("OWNED_SOLIDWORKS_PROCESS_NOT_FOUND");
            session.OwnedPid=after[0];
            using(var process=Process.GetProcessById(session.OwnedPid))session.OwnedStartTime=process.StartTime;
            Console.WriteLine($"OWNED_SOLIDWORKS_PID={session.OwnedPid}\nOWNED_SOLIDWORKS_START_TIME={session.OwnedStartTime:O}");
            session.RequireGui();
            return session;
        }catch {
            if(acquired)gate.ReleaseMutex();gate.Dispose();
            // Leave any newly started server untouched for user inspection.
            throw;
        }
    }
    public void RequireGui()
    {
        var watch=Stopwatch.StartNew(); long frameHwnd=0; nint handle=0; string title="";
        using var process=Process.GetProcessById(OwnedPid);
        using var executor=Process.GetCurrentProcess();
        do {
            var current=Pids();
            if(current.Length!=1||current[0]!=OwnedPid)throw new InvalidOperationException("SOLIDWORKS_SESSION_OWNERSHIP_CHANGED");
            process.Refresh();
            if(process.HasExited||process.StartTime!=OwnedStartTime)throw new InvalidOperationException("OWNED_SOLIDWORKS_PROCESS_CHANGED");
            handle=process.MainWindowHandle;title=process.MainWindowTitle;
            try { var frame=Root?.Frame() as IFrame; frameHwnd=frame?.GetHWndx64() ?? 0; } catch { frameHwnd=0; }
            if(frameHwnd!=0)break;
            Thread.Sleep(250);
        }while(watch.Elapsed<TimeSpan.FromSeconds(15));
        bool visible=Root?.Visible ?? false;
        Console.WriteLine($"SOLIDWORKS_VISIBLE_PROPERTY={visible}\nPROCESS_MAIN_WINDOW_HANDLE={handle}\nSW_FRAME_AVAILABLE={frameHwnd!=0}\nSW_FRAME_HWND_X64={frameHwnd}\nOWNED_MAIN_WINDOW_TITLE={title}\nOWNED_SESSION_ID={process.SessionId}\nEXECUTOR_SESSION_ID={executor.SessionId}");
        Console.WriteLine($"RESPONDING={process.Responding}");
        GuiSessionReady=process.Responding && (handle!=0 || frameHwnd!=0);
        if(frameHwnd==0||!visible||!process.Responding||process.SessionId!=executor.SessionId)throw new InvalidOperationException("SOLIDWORKS_FRAME_GUI_NOT_EXPOSED");
    }
    public void Checkpoint(ModelDoc2 drawing,int viewCount,IEnumerable<double> scales,string target)
    {
        int errors=0;
        var root=Root ?? throw new InvalidOperationException("SOLIDWORKS_ROOT_UNAVAILABLE");
        var activated=root.ActivateDoc3(drawing.GetTitle(),false,(int)swRebuildOnActivation_e.swDontRebuildActiveDoc,ref errors) as ModelDoc2;
        var active=root.ActiveDoc as ModelDoc2;
        if(activated==null||active==null||active.GetType()!=(int)swDocumentTypes_e.swDocDRAWING||active.GetTitle()!=drawing.GetTitle())throw new InvalidOperationException($"ACTIVE_DRAWING_CHECKPOINT_FAILED:{errors}");
        root.Visible=true;RequireGui();
        using var process=Process.GetProcessById(OwnedPid);process.Refresh();
        Console.WriteLine($"EXECUTOR_PID={System.Environment.ProcessId}\nOWNED_SOLIDWORKS_PID={OwnedPid}\nSOLIDWORKS_VISIBLE={root.Visible}\nMAIN_WINDOW_HANDLE={process.MainWindowHandle}\nACTIVE_DOC_TYPE={active.GetType()}\nACTIVE_DOC_TITLE={active.GetTitle()}\nVIEW_COUNT={viewCount}\nSCALE={string.Join(",",scales)}\nMANUAL_SAVE_TARGET={target}");
    }
    public void Dispose()
    {
        GC.KeepAlive(Root);GC.KeepAlive(retainedRoot);
        if(ownsGate) gate.ReleaseMutex();
        gate.Dispose();
        // Never CloseDoc, ExitApp, FinalReleaseComObject or Kill here.
    }

    public void ShutdownOwned(IEnumerable<string>? documentTitles = null)
    {
        if (!ComSessionReady || Root is null) return;
        try
        {
            foreach (var title in documentTitles ?? Array.Empty<string>())
                try { Root.CloseDoc(title); } catch { }
            try { Root.ExitApp(); } catch { }
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (Pids().Length == 0) break;
                Thread.Sleep(250);
            }
            Console.WriteLine($"SOLIDWORKS_PROCESS_COUNT_AFTER={Pids().Length}");
        }
        finally { GC.KeepAlive(Root); }
    }
}

class AcceptedDrawingExecutionContext
{
    public VisibleSolidWorksSession Session { get; }
    public SldWorks Root { get; }
    public ModelDoc2 SourceModel { get; }
    public string OutputDirectory { get; }
    public string OutputStem { get; }
    public AcceptedDrawingExecutionContext(VisibleSolidWorksSession session, SldWorks root, ModelDoc2 sourceModel, string outputDirectory, string outputStem)
    { Session = session; Root = root; SourceModel = sourceModel; OutputDirectory = outputDirectory; OutputStem = outputStem; }
}

// Compatibility name retained for callers compiled against the earlier owned
// pipeline boundary.  It carries the same injected resources and does not own
// or acquire a SolidWorks instance.
sealed class OwnedSolidWorksContext : AcceptedDrawingExecutionContext
{
    public OwnedSolidWorksContext(VisibleSolidWorksSession session, SldWorks root, ModelDoc2 sourceModel, string outputDirectory, string outputStem)
        : base(session, root, sourceModel, outputDirectory, outputStem) { }
}
