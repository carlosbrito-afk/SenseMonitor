using System;
using System.Management;
using Forja.Core;

namespace Forja.Platform
{
    public enum RestorePointStatus
    {
        Created,
        ProtectionDisabled,
        FrequencyLimit,
        AccessDenied,
        Failed
    }

    public class RestorePointResult
    {
        public readonly RestorePointStatus Status;
        public readonly int Code;
        public readonly string Detail;

        public RestorePointResult(RestorePointStatus status, int code, string detail)
        {
            Status = status;
            Code = code;
            Detail = detail;
        }

        public bool Ok
        {
            get { return Status == RestorePointStatus.Created; }
        }

        // Says what happened and what to do. Shown next to the confirmation, not as a blocker.
        public string UserMessage
        {
            get
            {
                switch (Status)
                {
                    case RestorePointStatus.Created:
                        return "Ponto de restaura\u00e7\u00e3o criado.";
                    case RestorePointStatus.ProtectionDisabled:
                        return "A Prote\u00e7\u00e3o do Sistema est\u00e1 desligada, ent\u00e3o n\u00e3o foi criado ponto de restaura\u00e7\u00e3o. O app n\u00e3o liga isso sozinho. Para ligar, abra Prote\u00e7\u00e3o do Sistema nas configura\u00e7\u00f5es do Windows.";
                    case RestorePointStatus.FrequencyLimit:
                        return "O Windows s\u00f3 cria um ponto de restaura\u00e7\u00e3o a cada 24 horas e j\u00e1 existe um recente, ent\u00e3o nenhum novo foi criado. O ponto recente continua valendo.";
                    case RestorePointStatus.AccessDenied:
                        return "O Windows negou a cria\u00e7\u00e3o do ponto de restaura\u00e7\u00e3o. Abra o app como administrador e tente de novo.";
                    default:
                        return "N\u00e3o foi poss\u00edvel criar o ponto de restaura\u00e7\u00e3o" + (Code != 0 ? " (c\u00f3digo " + Code + ")" : "") + ". Voc\u00ea pode continuar sem ele, mas o Windows n\u00e3o poder\u00e1 voltar a este estado.";
                }
            }
        }
    }

    // Talks to WMI. Replaced by a fake in tests; never called during development.
    public interface IRestorePointBackend
    {
        RestorePointResult CreateRestorePoint(string description);
    }

    // What the pages see: "create a point for this action".
    public interface IRestorePointService
    {
        RestorePointResult Create(string action);
    }

    public class RestorePoint : IRestorePointService
    {
        public const string Prefix = "SenseMonitor: ";
        public const int MaxDescription = 64;

        private readonly IRestorePointBackend backend;
        private readonly ChangeLog log;

        public RestorePoint()
            : this(new WmiRestoreBackend(), null)
        {
        }

        public RestorePoint(ChangeLog log)
            : this(new WmiRestoreBackend(), log)
        {
        }

        public RestorePoint(IRestorePointBackend backend, ChangeLog log)
        {
            if (backend == null) throw new ArgumentNullException("backend");
            this.backend = backend;
            this.log = log;
        }

        public static string BuildDescription(string action)
        {
            string a = (action ?? "").Trim();
            if (a.Length == 0) a = "manuten\u00e7\u00e3o";
            string d = Prefix + a;
            if (d.Length > MaxDescription) d = d.Substring(0, MaxDescription);
            return d;
        }

        // returnValue: ReturnValue of the WMI method (-1 when the call threw);
        // hresult: HRESULT of the exception (0 when none);
        // newPointAppeared: whether a new sequence number showed up (null when unknown).
        public static RestorePointResult Classify(int returnValue, int hresult, bool? newPointAppeared)
        {
            uint h = unchecked((uint)hresult);
            if (hresult != 0)
            {
                if (h == 0x80070422u || h == 0x80070425u) return new RestorePointResult(RestorePointStatus.ProtectionDisabled, hresult, null);
                if (h == 0x80070005u || h == 0x80041003u) return new RestorePointResult(RestorePointStatus.AccessDenied, hresult, null);
                return new RestorePointResult(RestorePointStatus.Failed, hresult, null);
            }
            if (returnValue == 0)
            {
                if (newPointAppeared.HasValue && !newPointAppeared.Value)
                    return new RestorePointResult(RestorePointStatus.FrequencyLimit, 0, null);
                return new RestorePointResult(RestorePointStatus.Created, 0, null);
            }
            if (returnValue == 1058 || returnValue == 1060) return new RestorePointResult(RestorePointStatus.ProtectionDisabled, returnValue, null);
            if (returnValue == 5) return new RestorePointResult(RestorePointStatus.AccessDenied, returnValue, null);
            return new RestorePointResult(RestorePointStatus.Failed, returnValue, null);
        }

        // Never throws. Records in the change history only when a point was really created.
        public RestorePointResult Create(string action)
        {
            string desc = BuildDescription(action);
            RestorePointResult r;
            try
            {
                r = backend.CreateRestorePoint(desc);
                if (r == null) r = new RestorePointResult(RestorePointStatus.Failed, 0, "sem resposta");
            }
            catch (Exception ex)
            {
                r = new RestorePointResult(RestorePointStatus.Failed, 0, ex.Message);
            }
            if (r.Ok && log != null)
            {
                try { log.Record(ChangeKind.PontoRestauracao, "Ponto de restaura\u00e7\u00e3o criado: " + desc, null); }
                catch (Exception) { }
            }
            return r;
        }
    }

    // root\default SystemRestore.CreateRestorePoint(description, 12 MODIFY_SETTINGS, 100 BEGIN_SYSTEM_CHANGE).
    public class WmiRestoreBackend : IRestorePointBackend
    {
        public RestorePointResult CreateRestorePoint(string description)
        {
            long before = -1;
            try
            {
                ManagementScope scope = NewScope();
                before = MaxSequence(scope);
                using (ManagementClass mc = new ManagementClass(scope, new ManagementPath("SystemRestore"), null))
                using (ManagementBaseObject inParams = mc.GetMethodParameters("CreateRestorePoint"))
                {
                    inParams["Description"] = description;
                    inParams["RestorePointType"] = 12;
                    inParams["EventType"] = 100;
                    using (ManagementBaseObject outParams = mc.InvokeMethod("CreateRestorePoint", inParams, null))
                    {
                        int rv = Convert.ToInt32(outParams["ReturnValue"]);
                        long after = MaxSequence(scope);
                        bool? appeared = null;
                        if (before >= 0 && after >= 0) appeared = after > before;
                        return RestorePoint.Classify(rv, 0, appeared);
                    }
                }
            }
            catch (ManagementException mex)
            {
                return RestorePoint.Classify(-1, mex.HResult != 0 ? mex.HResult : (int)mex.ErrorCode, null);
            }
            catch (System.Runtime.InteropServices.COMException cex)
            {
                return RestorePoint.Classify(-1, cex.ErrorCode, null);
            }
            catch (UnauthorizedAccessException)
            {
                return RestorePoint.Classify(-1, unchecked((int)0x80070005), null);
            }
            catch (Exception ex)
            {
                return new RestorePointResult(RestorePointStatus.Failed, 0, ex.Message);
            }
        }

        private static ManagementScope NewScope()
        {
            ManagementScope scope = new ManagementScope(@"\\.\root\default");
            scope.Options.EnablePrivileges = true;
            scope.Connect();
            return scope;
        }

        // Highest SequenceNumber among existing points, or -1 when they cannot be listed.
        private static long MaxSequence(ManagementScope scope)
        {
            try
            {
                long max = 0;
                using (ManagementClass mc = new ManagementClass(scope, new ManagementPath("SystemRestore"), null))
                using (ManagementObjectCollection items = mc.GetInstances())
                {
                    foreach (ManagementObject o in items)
                    {
                        using (o)
                        {
                            long n = Convert.ToInt64(o["SequenceNumber"]);
                            if (n > max) max = n;
                        }
                    }
                }
                return max;
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}
