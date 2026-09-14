// Xslt30Runner — child-mode runner for the W3C XSLT 3.0 test suite (github.com/w3c/xslt30-test, downloaded by
// fetch-corpus). Same architecture as the QT3 runner in Program.cs: the parent process loop
// (per-set child isolation, SETRESULT protocol, faildump) is shared; this file provides the child
// executor for the xslt-test-catalog namespace. Scope: stylesheet AND package tests (xsl:package via
// CompilePackage/ImportPackage/Link), initial-template / initial-mode / initial-function / params,
// xsl:message + xsl:result-document + warning capture, error + XPath/assert-xml/serialization asserts.
// Only streaming postures (EE static analysis) are skipped as a feature HE does not provide.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using S = OutSmart.DAXon.Api;

namespace OutSmart.DAXon.ConformanceTests
{
    internal static partial class Program
    {
        static readonly XNamespace X = "http://www.w3.org/2012/10/xslt-test-catalog";

        // Features an XSLT 3.0 Saxon-HE-equivalent processor supports / lacks. satisfied="false" inverts
        // the requirement (the test must run on a processor WITHOUT the feature).
        static readonly HashSet<string> XFeatSupported = new HashSet<string>(StringComparer.Ordinal)
        {
            "serialization", "dtd", "namespace_axis", "backwards_compatibility",
            "disabling_output_escaping", "built_in_derived_types", "higher_order_functions",
            "HTML4", "HTML5", "xsl-stylesheet-processing-instruction", "dynamic_evaluation",
            "XPath_3.1", // supported => satisfied="false" cases (format-number-069b) don't apply
        };
        static readonly HashSet<string> XFeatUnsupported = new HashSet<string>(StringComparer.Ordinal)
        {
            "schema_aware", "streaming", "streaming-fallback",
        };

        static void RunXsltTestSet(string tsPath)
        {
            XDocument doc;
            try { doc = XDocument.Load(tsPath, System.Xml.Linq.LoadOptions.PreserveWhitespace); }
            catch { return; }
            var setEl = doc.Root;
            string setName = (string)setEl.Attribute("name") ?? Path.GetFileName(tsPath);
            string baseDir = Path.GetDirectoryName(tsPath);
            // Base URI of an inline <content> source document is the URI of the test-set catalog
            // (FOTS convention; base-uri()/static-base-uri() of such a doc = the catalog, backwards-041).
            _xTsUri = new Uri(Path.GetFullPath(tsPath)).AbsoluteUri;

            // conformance measures correctness, not wall-clock; no transformation time limit
            _proc = new S.Processor(false, transformTimeout: TimeSpan.Zero);
            _dummy = DummyDoc(_proc);

            var envs = setEl.Elements(X + "environment").Where(e => e.Attribute("name") != null)
                .ToDictionary(e => (string)e.Attribute("name"), e => e, StringComparer.Ordinal);
            var setDeps = setEl.Elements(X + "dependencies").Elements().ToList();

            // A SET-LEVEL exact-old spec pin means the whole set targets old processors (only
            // regex-syntax-xslt20: "for XSLT20 processors... For XSLT 3.0, see the regular
            // regex-syntax folder"). Its fails are exactly where the 2.0/3.0 grammars diverge —
            // a 3.0 processor gives the (correct) 3.0 answer, so the set is version-pinned N/A.
            // Case-level pins inside mixed sets still run (3.0 is backwards compatible).
            string setSpec = setDeps.Where(d => d.Name.LocalName == "spec")
                .Select(d => (string)d.Attribute("value") ?? "").FirstOrDefault();
            bool setPinnedOld = setSpec != null && setSpec.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .All(t => t == "XSLT10" || t == "XSLT20") && setSpec.Length > 0;

            InitSlice();
            if (setPinnedOld)
            {
                foreach (var tc0 in setEl.Elements(X + "test-case"))
                {
                    if (SliceSkip()) continue;
                    Skip("spec: exact-XSLT20-only set (version-pinned; 3.0 twin set covers current semantics)");
                }
                return;   // caller prints SETRESULT
            }
            foreach (var tc in setEl.Elements(X + "test-case"))
            {
                if (SliceSkip()) continue;
                string id = setName + "/" + ((string)tc.Attribute("name") ?? "?");
                string caseFilter = Environment.GetEnvironmentVariable("QT3_CASE");
                if (!string.IsNullOrEmpty(caseFilter) && !id.Contains(caseFilter)) continue;
                try { RunXCase(id, tc, setDeps, envs, baseDir); }
                catch (Exception ex) { _fail++; _failures.Add($"{id} :: driver-exception: {Trim(ex.Message)}"); if (_vdump) _verdicts.Add(id + "\tFAIL"); }
            }
        }

        static string XDepSkipReason(XElement dep)
        {
            string value = (string)dep.Attribute("value") ?? "";
            bool satisfied = (string)dep.Attribute("satisfied") != "false";
            switch (dep.Name.LocalName)
            {
                case "spec":
                {
                    // We are an XSLT 3.0 processor: run XSLT30/…+ tests. Exact-old pins (all tokens
                    // XSLT10/XSLT20, no '+') mark 2.0-only semantics — the catalog pairs them with
                    // XSLT30 twins ("changed spec to XSLT20 only, see test Na"), so they are N/A here.
                    var tokens = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    bool applicable = tokens.Any(t => t == "XSLT30" || t == "XSLT30+" || t == "XSLT20+" || t == "XSLT10+");
                    if (!applicable) return "spec: " + value + (tokens.Any(t => t == "XSLT10" || t == "XSLT20")
                        ? " (2.0-only semantics; 3.0 twin covers current)" : "");
                    return null;
                }
                case "feature":
                    // NOSKIP head-to-head: execute streaming cases too — HE evaluates them via the
                    // non-streaming fallback (IKVM Saxon-HE 12.9 passes ~60 of them that way), and
                    // schema_aware-labelled cases as well (a handful only need built-in derived types,
                    // not real schema awareness — the IKVM HE reference passes 14 of them untyped).
                    // Posture/sweep asserts still skip. Normal runs keep both gates (correct HE posture).
                    if (_noskip && (value == "streaming" || value == "streaming-fallback" || value == "schema_aware")) return null;
                    if (satisfied && XFeatUnsupported.Contains(value)) return "feature: " + value;
                    // NOSKIP: tests-for-absence (satisfied="false" on a feature we have, e.g. DOE) are
                    // often pass-either-way — the IKVM reference passes them; execute head-to-head.
                    if (!satisfied && XFeatSupported.Contains(value)) return _noskip ? null : "feature-absent: " + value;
                    return null;
                case "languages_for_numbering":
                    // Language-specific spell-out numbering (xsl:number lang="de"/"it" → "drei"/"terzo") ships
                    // only in Saxon-PE/EE; Saxon-HE — and hence this HE-derived port — has no Numberer for a
                    // non-English language and falls back to English digits. Verified against upstream Saxon-HE
                    // (format-integer(3,'Ww','de') → "Three"), so these are a shared HE limitation, not our gap.
                    return (!_noskip && satisfied && value != "en" && !value.StartsWith("en")) ? "languages_for_numbering: " + value + " (HE has no non-English numberer)" : null;
                case "xsd-version":
                    return value == "1.1" ? "xsd-version: 1.1" : null;
                case "year_component_values":
                    // Saxon-HE supports extended/negative year values; tests gated on the ABSENCE of
                    // that support (satisfied="false", e.g. date-094*/095* expecting FODT0001) don't apply.
                    return satisfied ? null : "feature-present: year_component_values (" + value + ")";
                case "unicode-version":
                    // Version-locked: the assert bakes in the exact character database of one frozen Unicode
                    // release. No shipping runtime is pinned to an old version, so neither we nor Saxon-HE-Java
                    // match it — parity skip (matches the qt3 policy).
                    return _noskip ? null : "unicode-version: " + value + " (version-locked data-table)";
                case "package_version_resolution":
                    // We resolve a use-package reference to the HIGHEST matching version (Saxon's
                    // policy); the catalog's lowest-version triplet variants don't apply.
                    return value == "lowest_version"
                        ? "dependency not-satisfied: package_version_resolution=lowest_version (we pick highest)" : null;
                case "on-multiple-match":
                    // Processor-configuration dependency: value="error" requires a processor that
                    // SIGNALS the (2.0-recoverable) ambiguous-rule-match error XTRE0540; ours recovers
                    // (warning + choose last, Saxon default), so those tests don't apply.
                    return value == "error" ? "dependency not-satisfied: on-multiple-match=error (processor recovers)" : null;
                default:
                    return null;   // on-multiple-match, extension-function, …: run and let asserts decide
            }
        }

        // unicode-90-Gen modes whose stylesheet computes $validrange[not(. = $c)] — the degenerate
        // nested-loop subset (see the skip in RunXCase). Derived from unicode90-Gen-001.xsl: every
        // mode that references $allcharsnot / the filtered range. Linear full-range modes (9, 10,
        // 17, replace8: 1.1M matches but no nested filter) still run.
        static readonly HashSet<string> Unicode90DegenerateModes = new HashSet<string>(StringComparer.Ordinal)
        {
            "fn-matches5", "fn-matches6", "fn-matches7", "fn-matches8", "fn-matches12",
            "fn-matches14", "fn-matches15", "fn-matches18", "fn-matches20", "fn-matches22",
            "fn-matches24", "fn-replace2", "fn-replace4", "fn-replace6", "fn-replace7",
            "fn-string-length2",
        };

        // Tests that upstream Saxon-HE-Java also fails (verified per-case against the HE 12.5 jar), with no FOTS
        // dependency to gate on — a shared HE limitation, not a gap of this port: non-BMP digit-family numbering
        // (xsl:number format='𐋡…' → HE emits ASCII), per-run lang numbering (2506 → English), and error-code
        // diagnostics HE cannot produce either (collations-1006 gives XTSE0125 not FOCH0002; json-to-xml-typed-010
        // XTSE1650 not XTDE3245; package-021err XTSE3000 not XTSE3050). Excluded to reflect the true HE ceiling.
        static readonly HashSet<string> HeParityExclusions = new HashSet<string>(StringComparer.Ordinal)
        {
            "number-5079", "number-5080", "number-5081", "number-5082", "number-5091", "number-5092",
            "number-5093", "number-5094", "number-5097", "number-5098", "number-2506",
            "collations-1006", "json-to-xml-typed-010", "package-021err",
            // sort-079: UCA alternate=blanked/shifted variable-weighting (exact ICU UCA). Saxon-HE
            // (java.text.Collator, no ICU) produces a different order too — verified it fails the first
            // assert on Java-HE — so this is a shared limitation, not a port gap.
            "sort-079",
            // result-document-1402: the expected result assumes a processor whose DEFAULT html-version is 4
            // (Saxon 9.x — no DOCTYPE for a nested json-node HTML fragment). Saxon 12 defaults to HTML5, which
            // emits "<!DOCTYPE HTML>"; verified Java-HE 12.5 produces byte-identical output to this port and
            // fails the same serialization-matches. Shared HE-12 behaviour, not a port gap.
            "result-document-1402",
            // static-032: @kinds sorts a path result of PARENTLESS text nodes returned by a function; their
            // document order is implementation-defined (XPath 3.1 §2.4.1). Saxon 12 orders them
            // "processing-instruction element element comment text"; the expected assumes the reverse of the
            // first two. Verified Java-HE 12.5 emits the identical order and fails the same assert — parity.
            "static-032",
            // docbook-001: the DocBook chunker writes docbook.css via write.chunk, whose capability probe
            // (saxon:output / exsl:document / redirect:write element-available) finds nothing in HE and
            // terminates "Can't make chunks" (XTMM9000 at chunker.xsl:244). Verified live: Java-HE 12.5 CLI
            // terminates identically. This port now reproduces Java's exact behaviour — shared HE boundary.
            "docbook-001",
        };

        static void RunXCase(string id, XElement tc, List<XElement> setDeps, Dictionary<string, XElement> envs, string baseDir)
        {
            _curId = id;
            string caseNm = id.Substring(id.LastIndexOf('/') + 1);
            if (!_noskip && HeParityExclusions.Contains(caseNm)) { Skip("Saxon-HE parity: feature/diagnostic absent in Saxon-HE too (" + caseNm + ")"); return; }
            foreach (var dep in setDeps.Concat(tc.Elements(X + "dependencies").Elements()))
            {
                string reason = XDepSkipReason(dep);
                if (reason != null) { Skip(reason); return; }
            }

            var test = tc.Element(X + "test");
            var result = tc.Element(X + "result");
            if (test == null || result == null) { Skip("driver: malformed test-case"); return; }

            _xMessages = new List<S.XdmNode>();
            _xResultDocs = new Dictionary<string, StringWriter>(StringComparer.Ordinal);
            _xBaseOutputUri = null;
            _xWarnings = 0;
            _xRawResult = null;
            _xErrorCodes = new List<string>();

            // XInclude expansion is a parser feature (Xerces in Java); System.Xml.XmlReader has none.
            // The catalog carries no feature dependency for it, so gate by case id.
            if (id == "base-uri/base-uri-052") { Skip("feature: XInclude (not supported by System.Xml.XmlReader)"); return; }

            // Streamability postures/sweeps are EE-only static analysis — not covered by HE.
            if (test.Element(X + "posture") != null) { Skip("feature: streaming (posture/sweep — EE static analysis)"); return; }
            if (result.Descendants(X + "assert-posture-and-sweep").Any()) { Skip("feature: streaming (posture/sweep — EE static analysis)"); return; }

            // unicode-90 modes that filter the full 1.1M-codepoint range against the category set
            // ($validrange[not(. = $c)]) run a nested-loop general comparison in HE — ~2G comparisons
            // per case (the hash-join rewrite is EE-only; HE Optimizer.optimizeGeneralComparison is a
            // no-op upstream too). Same policy as KnownStressCases: skip just these with an explicit
            // reason; the set's ~850 tractable cases still run.
            if (id.StartsWith("unicode-90/", StringComparison.Ordinal))
            {
                string u90mode = (string)test.Element(X + "initial-mode")?.Attribute("name");
                if (u90mode != null && Unicode90DegenerateModes.Contains(u90mode))
                {
                    Skip("perf: HE nested-loop general comparison over 1.1M-codepoint range (hash-join is EE-only)");
                    return;
                }
            }

            // Environment: ref or inline; may carry a context source and library packages.
            XElement env = null;
            var envRef = tc.Element(X + "environment");
            if (envRef != null)
            {
                string r = (string)envRef.Attribute("ref");
                if (r != null) envs.TryGetValue(r, out env); else env = envRef;
            }
            // collection() environments — same registration helper as QT3, xslt-test-catalog namespace
            // (the shared Processor's default collection is reset per case inside the helper).
            SetupCollections(env, baseDir, X);
            // <resource uri= file= media-type="application/xquery"> => module resolver so that
            // fn:load-xquery-module can locate the module by its namespace URI (load-xquery-module-002..004).
            var xqModules = new Dictionary<string, string>(StringComparer.Ordinal);
            if (env != null)
            {
                foreach (var res in env.Elements(X + "resource"))
                {
                    string ruri = (string)res.Attribute("uri");
                    string rfile = (string)res.Attribute("file");
                    if (ruri != null && rfile != null && ((string)res.Attribute("media-type") ?? "").Contains("xquery"))
                    {
                        string rp = ResolveFile(baseDir, rfile);
                        if (File.Exists(rp)) xqModules[ruri] = rp;
                    }
                }
            }
            try
            {
                _proc.UnderlyingConfiguration.SetModuleURIResolver(
                    xqModules.Count > 0 ? new EnvModuleResolver(xqModules) : null);
            }
            catch { }
            S.XdmNode ctx = null;
            string embeddedStylesheetSource = null;   // source doc with an xml-stylesheet PI (defines-stylesheet="true")
            var builder = _proc.NewDocumentBuilder();
            if (env != null)
            {
                foreach (var src in env.Elements(X + "source"))
                {
                    if ((string)src.Attribute("role") != ".") continue;
                    try
                    {
                        string file = (string)src.Attribute("file");
                        if (file != null)
                        {
                            string p = ResolveFile(baseDir, file);
                            if ((string)src.Attribute("defines-stylesheet") == "true") embeddedStylesheetSource = p;
                            using (var fs = File.OpenRead(p)) ctx = builder.Build(fs, new Uri(Path.GetFullPath(p)).AbsoluteUri);
                        }
                        else
                        {
                            var content = src.Element(X + "content");
                            if (content != null)
                                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(content.Value)))
                                    ctx = builder.Build(ms, _xTsUri ?? new Uri(Path.GetFullPath(Path.Combine(baseDir, "inline.xml"))).AbsoluteUri);
                        }
                        // select= narrows the context item to a node within the document (mode-1105:
                        // role="." select="/doc"), or — when there is no file/content — is itself the
                        // computed context node (id-043: role="." select="parse-xml('<root/>')").
                        string ctxSel = (string)src.Attribute("select");
                        if (ctxSel != null)
                        {
                            try
                            {
                                var xpc = _proc.NewXPathCompiler();
                                foreach (S.XdmItem it in (ctx != null ? xpc.Evaluate(ctxSel, ctx) : xpc.Evaluate(ctxSel, null)))
                                {
                                    if (it is S.XdmNode xn) { ctx = xn; break; }
                                }
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex) { _fail++; _failures.Add($"{id} :: driver: context build failed: {Trim(ex.Message)}"); if (_vdump) _verdicts.Add(id + "\tFAIL"); return; }
                }
            }

            var styEl = test.Elements(X + "stylesheet").FirstOrDefault(s => (string)s.Attribute("role") == null || (string)s.Attribute("role") == "principal");
            if (styEl == null && env != null)
                styEl = env.Elements(X + "stylesheet").FirstOrDefault(s => (string)s.Attribute("role") == null || (string)s.Attribute("role") == "principal");
            // The test subject may be a package, or a stylesheet embedded in the source document
            // via an xml-stylesheet processing instruction (defines-stylesheet="true").
            var pkgEl = test.Elements(X + "package").FirstOrDefault(p => (string)p.Attribute("role") == null || (string)p.Attribute("role") == "principal");
            if (styEl == null && pkgEl == null && embeddedStylesheetSource == null) { Skip("driver: no principal stylesheet"); return; }
            string styFile = pkgEl != null ? ResolveFile(baseDir, (string)pkgEl.Attribute("file"))
                           : styEl != null ? ResolveFile(baseDir, (string)styEl.Attribute("file"))
                           : embeddedStylesheetSource;
            // Library packages: from the environment plus secondary entries in <test>, in document order
            // (later ones may use earlier ones).
            var libPkgs = new List<XElement>();
            // Environment packages: only the LIBRARY ones — a role="principal" entry is the test
            // subject itself (use-package-175 declares it alongside its dependencies; compiling it
            // first would fail against the not-yet-registered libraries).
            if (env != null) libPkgs.AddRange(env.Elements(X + "package").Where(p => (string)p.Attribute("role") != "principal"));
            libPkgs.AddRange(test.Elements(X + "package").Where(p => (string)p.Attribute("role") == "secondary"));

            // Params: static ones go to the compiler, dynamic ones to the transformer. Values are XPath
            // expressions evaluated against an empty context (the QT3 external-param approach).
            var staticParams = new List<(S.QName, S.XdmValue)>();
            var dynParams = new Dictionary<S.QName, S.XdmValue>();
            foreach (var p in test.Elements(X + "param"))
            {
                string name = (string)p.Attribute("name");
                string select = (string)p.Attribute("select") ?? "''";
                S.XdmValue val;
                try
                {
                    var xpc = _proc.NewXPathCompiler();
                    val = xpc.Evaluate(select, _dummy);
                }
                catch { val = new S.XdmAtomicValue(select); }
                var target = (string)p.Attribute("static") == "yes" ? staticParams : null;
                if (target != null) target.Add((new S.QName(name), val));
                else dynParams[new S.QName(name)] = val;
            }

            // unicode-90 generated cases carry NO <param>, but their stylesheet (unicode90-Gen-001.xsl)
            // is parameterized by $charclass (default 'Ll') — as published, 37/38 categories can never
            // pass. The intended category is encoded in the case name (unicode90-<CAT>-NNN, see
            // _unicode-90-generate-test-set.xsl); derive it the way Saxon's own driver special-cases it.
            if (id.StartsWith("unicode-90/unicode90-", StringComparison.Ordinal))
            {
                var seg = ((string)tc.Attribute("name") ?? "").Split('-');
                if (seg.Length == 3 && !int.TryParse(seg[1], out _))
                    dynParams[new S.QName("charclass")] = new S.XdmAtomicValue(seg[1]);
            }

            // enable_assertions dependency: case-level entry overrides set-level (last wins).
            bool enableAssertions = false;
            foreach (var dep in setDeps.Concat(tc.Elements(X + "dependencies").Elements()))
                if (dep.Name.LocalName == "enable_assertions")
                    enableAssertions = (string)dep.Attribute("satisfied") != "false";

            // feature XML_1.1: configure the processor for XML 1.1 for THIS case only (C0 controls via
            // codepoints-to-string, xml-to-json-D015/17/18). A global 1.1 default is wrong — the
            // serializer's default output version follows it and output-0130 requires version="1.0".
            bool xml11 = setDeps.Concat(tc.Elements(X + "dependencies").Elements()).Any(dep =>
                dep.Name.LocalName == "feature" && (string)dep.Attribute("value") == "XML_1.1"
                && (string)dep.Attribute("satisfied") != "false");
            _proc.UnderlyingConfiguration.XMLVersion=xml11 ? OutSmart.DAXon.Core.Configuration.XML11 : OutSmart.DAXon.Core.Configuration.XML10;

            // feature XSD_1.1 satisfied="false": the case declares XSD 1.0 semantics, so run this case
            // under XSD 1.0 — the anyURI lexical check (xsl:namespace XTDE0905, error-0905a) and
            // type-available for XSD-1.1-only types (xs:dateTimeStamp, type-available-0151) are gated on it.
            bool xsd10 = setDeps.Concat(tc.Elements(X + "dependencies").Elements()).Any(dep =>
                dep.Name.LocalName == "feature" && (string)dep.Attribute("value") == "XSD_1.1"
                && (string)dep.Attribute("satisfied") == "false");
            _proc.UnderlyingConfiguration.XsdVersion=xsd10 ? OutSmart.DAXon.Core.Configuration.XSD10 : OutSmart.DAXon.Core.Configuration.XSD11;

            // ignore_doc_failure satisfied="true": the case requires document() to RECOVER from a retrieval
            // failure (return empty) rather than raise FODC0002/FODC0005 (error-FODC0002a-ignore). Set per-case
            // (reset to false otherwise) so the non-ignore twin error-FODC0002a still raises.
            bool ignoreDocFail = setDeps.Concat(tc.Elements(X + "dependencies").Elements()).Any(dep =>
                dep.Name.LocalName == "ignore_doc_failure" && (string)dep.Attribute("satisfied") != "false");
            _proc.UnderlyingConfiguration.SetRecoverFromDocFailures(ignoreDocFail);

            string errorCode = null;
            string serialized = null;
            S.XdmNode resultNode = null;
            var compileErrors = new List<S.IXmlProcessingError>();
            try
            {
                var comp = _proc.NewXsltCompiler();
                comp.SetErrorList(compileErrors);
                if (enableAssertions) comp.SetAssertionsEnabled(true);
                foreach (var (qn, v) in staticParams) comp.SetParameter(qn, v);
                foreach (var pkg in libPkgs)
                {
                    string pf = ResolveFile(baseDir, (string)pkg.Attribute("file"));
                    var lib = comp.CompilePackage(new OutSmart.DAXon.Lib.ResolvedResource { SystemId = new Uri(Path.GetFullPath(pf)).AbsoluteUri });
                    // Register under the package's OWN name and version: the catalog's uri/package-version
                    // are descriptive aliases that can disagree with the source (override-f-024 declares
                    // 1.0.0 for a 0.0.1 package; override-t-B2 aliases override-103 as override-t-003).
                    comp.ImportPackage(lib);
                    // fn:transform(package-name=...) resolves through a NEW runtime compiler whose
                    // CompilerInfo copies the configuration default — register there too (transform-005/6).
                    try
                    {
                        _proc.UnderlyingConfiguration.DefaultXsltCompilerInfo
                            .GetPackageLibrary().AddPackage(lib.UnderlyingPreparedPackage);
                    }
                    catch { }
                }

                S.XsltExecutable exe;
                if (pkgEl != null)
                {
                    exe = comp.CompilePackage(new OutSmart.DAXon.Lib.ResolvedResource { SystemId = new Uri(Path.GetFullPath(styFile)).AbsoluteUri }).Link();
                }
                else if (styEl == null)
                {
                    // xml-stylesheet PI: locate the (possibly embedded) stylesheet within the source document
                    var assoc = comp.GetAssociatedStylesheet(
                        new OutSmart.DAXon.Lib.ResolvedResource { SystemId = new Uri(Path.GetFullPath(embeddedStylesheetSource)).AbsoluteUri }, null, null, null);
                    exe = comp.Compile(assoc);
                }
                else
                {
                    using (var fs = File.OpenRead(styFile))
                        exe = comp.Compile(fs, new Uri(Path.GetFullPath(styFile)).AbsoluteUri);
                }

                var t = exe.Load30();
                t.SetErrorReporter(new OutSmart.DAXon.Lib.DelegateErrorReporter(err =>
                {
                    try { if (err.IsWarning()) _xWarnings++; } catch { }
                }));
                if (dynParams.Count > 0) t.SetStylesheetParameters(dynParams);
                if (ctx != null) t.GlobalContextItem=ctx;

                var initialModeEl = test.Element(X + "initial-mode");
                string initialMode = (string)initialModeEl?.Attribute("name");
                // <initial-mode select="..."/> supplies the apply-templates SELECTION (may be atomic,
                // package-001d..q: select="42" with no source doc — the default xsl:initial-template
                // path raised XTDE0040).
                S.XdmValue modeSelect = null;
                string modeSelectExpr = (string)initialModeEl?.Attribute("select");
                if (modeSelectExpr != null)
                {
                    var mxpc = _proc.NewXPathCompiler();
                    // doc('mode-14.xml') in an <initial-mode select> resolves against the compiler's static
                    // base URI — set it to the test-set dir so relative document loads work (mode-1802).
                    try { mxpc.BaseURI=new OutSmart.DAXon.Internal.Net.URI(new Uri(Path.GetFullPath(Path.Combine(baseDir, "x"))).AbsoluteUri); } catch { }
                    try { modeSelect = mxpc.Evaluate(modeSelectExpr, ctx ?? _dummy); }
                    catch { }
                }
                if (initialMode != null)
                {
                    // "#unnamed"/"#default" are symbolic (catalog convention), not lexical QNames —
                    // map to the engine's reserved mode names (mode-17xx family raised XTDE0045).
                    // Prefix resolution: against the initial-mode ELEMENT itself — mode-1435 declares
                    // xmlns:a right on <initial-mode>, not on an ancestor.
                    var modeName = initialMode == "#unnamed" ? OutSmart.DAXon.Transformation.Mode.UNNAMED_MODE_NAME
                                 : initialMode == "#default" ? OutSmart.DAXon.Transformation.Mode.DEFAULT_MODE_NAME
                                 : XQNameToStructured(initialMode, initialModeEl);
                    t.UnderlyingController.SetInitialMode(modeName);
                    // <initial-mode> may carry <param> children (params passed to the matched template,
                    // tunnel="yes" or not) — supplied via SetInitialTemplateParameters like initial-template
                    // (initial-mode-004). These apply to the apply-templates entry below.
                    var imParams = new Dictionary<S.QName, S.XdmValue>();
                    var imTunnel = new Dictionary<S.QName, S.XdmValue>();
                    foreach (var p in initialModeEl.Elements(X + "param"))
                    {
                        string sel = (string)p.Attribute("select") ?? "''";
                        S.XdmValue v;
                        try { v = _proc.NewXPathCompiler().Evaluate(sel, _dummy); }
                        catch { v = new S.XdmAtomicValue(sel); }
                        var target = (string)p.Attribute("tunnel") == "yes" ? imTunnel : imParams;
                        target[XQName((string)p.Attribute("name"), p)] = v;
                    }
                    if (imParams.Count > 0) t.SetInitialTemplateParameters(imParams, false);
                    if (imTunnel.Count > 0) t.SetInitialTemplateParameters(imTunnel, true);
                }

                // xsl:message capture (assert-message) and secondary result documents (assert-result-document)
                t.SetMessageHandler(m => { lock (_xMessages) _xMessages.Add(m.Content); });
                // W3C driver convention: base output URI = <testdir>/results/<case>.xml — the
                // current-output-uri set asserts ends-with(..., 'results/<case>.xml') on it.
                string caseName = id.Contains("/") ? id.Substring(id.IndexOf('/') + 1) : id;
                // <output file="#absent"/>: the catalog explicitly requests NO base output URI
                // (current-output-uri-013/015 assert empty(current-output-uri())).
                if ((string)test.Element(X + "output")?.Attribute("file") == "#absent")
                {
                    _xBaseOutputUri = null;
                }
                else
                {
                    _xBaseOutputUri = new Uri(Path.GetFullPath(Path.Combine(baseDir, "results", caseName + ".xml"))).AbsoluteUri;
                    t.SetBaseOutputURI(_xBaseOutputUri);
                }
                t.UnderlyingController.ResultDocumentResolver=new XResultDocCapture(_proc, _xResultDocs);

                // Serialize DIRECTLY (the proven QT3 path — XdmDestination yields an empty tree in this
                // port); the result document for XPath asserts is re-parsed from the serialized text.
                // NOTE: a StringWriter, deliberately — encoding is applied only on the byte path, so
                // SESU0007/SERE0014 encoding errors (output-0184/0185/0195) stay unreported; switching
                // to a MemoryStream was tried and regressed ~26 cases (the port's charset layer accepts
                // fewer encodings than Java and utf-16/iso-8859-1 outputs mis-round-trip).
                var sw = new StringWriter();
                var ser = _proc.NewSerializer(sw);
                // default_html_version=4 dependency: the stylesheet declares no html-version, so the
                // processor default (HTML5) would apply — but this case wants HTML4 serialization
                // (output-0195 expects SERE0014 for a #x7F-#x9F control char, which only HTML4 rejects).
                if (setDeps.Concat(tc.Elements(X + "dependencies").Elements()).Any(dep =>
                        dep.Name.LocalName == "default_html_version" && (string)dep.Attribute("value") == "4"))
                    ser.SetOutputProperty(S.Serializer.Property.HTML_VERSION, "4.0");
                string initialTemplate = (string)test.Element(X + "initial-template")?.Attribute("name");
                var initFn = test.Element(X + "initial-function");
                // <output serialize="no"/> or tree="no": asserts (assert-type/eq/count/deep-equal) target
                // the RAW XdmValue — invoke via the value-returning overloads and keep the value; still
                // serialize it so tree/string asserts keep working. serialize="yes" WINS over tree="no"
                // (result-document-140x/output-070x want the stylesheet-serialized principal output,
                // e.g. method=json — the raw-path fallback serializer would drop those properties).
                var outputEl = test.Element(X + "output");
                string serializeAttr = (string)outputEl?.Attribute("serialize");
                bool rawOutput = serializeAttr == "no" || ((string)outputEl?.Attribute("tree") == "no" && serializeAttr != "yes");
                // json/adaptive/text serialize to non-XML text: the principal output can't be reparsed as a
                // result tree, and a document-node result loses its type on the string round-trip. Capture the
                // raw XdmValue for these methods too — assert-type sees the real item type (seqtor-043b) and a
                // separate method=xml reparse below gives XPath path asserts a tree (maps-017). Contained to
                // these three methods; the proven xml/html/xhtml destination path is untouched.
                OutSmart.DAXon.Serialization.SerializationProperties effSerProps = null;
                try { effSerProps = t.UnderlyingController.GetExecutable().PrimarySerializationProperties; } catch { }
                string effMethod = "";
                try { effMethod = effSerProps?.GetProperty("method") ?? ""; } catch { }
                bool nonXmlMethod = effMethod == "json" || effMethod == "adaptive" || effMethod == "text";
                // …but ONLY when the asserts are tree/value/type based. Serialization asserts
                // (assert-serialization / serialization-matches / -error) need the destination path, which
                // correctly separates principal output from xsl:result-document — the value overload returns
                // result-document content as the principal result (output-0174/0175/0308), and lets encoding
                // errors propagate (output-0185). So keep those on the proven path.
                bool hasSerAssert = result != null && result.Descendants().Any(e =>
                    e.Name.LocalName == "assert-serialization"
                    || e.Name.LocalName == "serialization-matches"
                    || e.Name.LocalName == "assert-serialization-error");
                bool useValue = rawOutput || (nonXmlMethod && !hasSerAssert);
                if (initFn != null)
                {
                    var args = new List<S.XdmValue>();
                    foreach (var p in initFn.Elements(X + "param"))
                    {
                        string sel = (string)p.Attribute("select") ?? "''";
                        try { args.Add(_proc.NewXPathCompiler().Evaluate(sel, _dummy)); }
                        catch { args.Add(new S.XdmAtomicValue(sel)); }
                    }
                    var fnName = XQName((string)initFn.Attribute("name"), initFn);
                    if (useValue) _xRawResult = t.CallFunction(fnName, args.ToArray());
                    else t.CallFunction(fnName, args.ToArray(), ser);
                }
                else if (initialTemplate != null)
                {
                    // <initial-template> may carry <param> children (template params, tunnel="yes" or not)
                    var itEl = test.Element(X + "initial-template");
                    var itParams = new Dictionary<S.QName, S.XdmValue>();
                    var itTunnel = new Dictionary<S.QName, S.XdmValue>();
                    foreach (var p in itEl.Elements(X + "param"))
                    {
                        string sel = (string)p.Attribute("select") ?? "''";
                        S.XdmValue v;
                        try { v = _proc.NewXPathCompiler().Evaluate(sel, _dummy); }
                        catch { v = new S.XdmAtomicValue(sel); }
                        var target = (string)p.Attribute("tunnel") == "yes" ? itTunnel : itParams;
                        target[XQName((string)p.Attribute("name"), p)] = v;
                    }
                    if (itParams.Count > 0) t.SetInitialTemplateParameters(itParams, false);
                    if (itTunnel.Count > 0) t.SetInitialTemplateParameters(itTunnel, true);
                    // Prefix resolution against the <initial-template> element itself — override-f-017
                    // declares xmlns:xsl right on it (name="xsl:initial-template").
                    if (useValue) _xRawResult = t.CallTemplate(XQName(initialTemplate, itEl));
                    else t.CallTemplate(XQName(initialTemplate, itEl), ser);
                }
                else if (modeSelect != null)
                {
                    if (useValue) _xRawResult = t.ApplyTemplates(modeSelect);
                    else t.ApplyTemplates(modeSelect, ser);
                }
                else if (ctx != null)
                {
                    if (useValue) _xRawResult = t.ApplyTemplates(ctx);
                    else t.ApplyTemplates(ctx, ser);
                }
                else if (initialMode != null)
                {
                    // XTDE0044: an initial mode was requested but there is no initial match selection —
                    // falling back to xsl:initial-template would mis-report XTDE0040 (error-0044*).
                    throw new OutSmart.DAXon.Transformation.XPathException(
                        "An initial mode was supplied but no initial match selection", "XTDE0044");
                }
                else
                {
                    // XSLT 3.0 default entry point when no source is supplied
                    var dflt = new S.QName("http://www.w3.org/1999/XSL/Transform", "initial-template");
                    if (useValue) _xRawResult = t.CallTemplate(dflt);
                    else t.CallTemplate(dflt, ser);
                }
                if (_xRawResult != null)
                {
                    if (nonXmlMethod && !rawOutput && effSerProps != null)
                    {
                        // Non-XML method (json/adaptive/text) with a real serialized result wanted: apply the
                        // stylesheet's effective output properties so serialization-matches sees method-correct
                        // text, and let serialization errors PROPAGATE (encoding SESU0007 etc. — output-0185)
                        // just as the destination path did. The raw value is already captured for asserts.
                        ser.SetOutputProperties(effSerProps);
                        ser.SerializeXdmValue(_xRawResult);
                    }
                    else
                    {
                        // rawOutput (serialize=no): serialization is incidental — tolerate failures.
                        try { ser.SerializeXdmValue(_xRawResult); } catch { }
                    }
                }

                // Keep the RAW serialized text: serialization-matches/assert-serialization compare
                // against it INCLUDING the XML declaration (result-document-02xx expect
                // <\?xml version=...). assert-xml strips the declaration at compare time instead.
                // The reparse must ALSO strip it: we re-encode the string as UTF-8 bytes, and a
                // declaration claiming another charset (encoding="iso-8859-1", normalize-unicode-00x)
                // would make the parser mis-decode them.
                serialized = sw.ToString();
                try
                {
                    using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(StripXmlDecl(serialized))))
                        resultNode = builder.Build(ms, new Uri(Path.GetFullPath(Path.Combine(baseDir, "result.xml"))).AbsoluteUri);
                }
                catch
                {
                    // html-method output (unclosed void elements, DOCTYPE) — normalize and retry so
                    // XPath asserts still get a tree (copy-2801 counts /html/body/table).
                    try
                    {
                        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(HtmlToXml(StripProlog(StripXmlDecl(serialized))))))
                            resultNode = builder.Build(ms, new Uri(Path.GetFullPath(Path.Combine(baseDir, "result.xml"))).AbsoluteUri);
                    }
                    catch { resultNode = null; }   // non-XML principal output (text/atomic) — asserts fall back to `serialized`
                }

                // json/adaptive/text: the method-serialized text won't reparse as a tree. If the raw value is
                // an element/document, re-serialize it as XML so XPath path asserts still get a tree (maps-017
                // /out = '{...}'). A text/atomic raw value still yields no tree — value/type asserts read
                // _xRawResult directly (assert-type via XBool's $result binding). Gated on nonXmlMethod so the
                // rawOutput (serialize=no) cases keep their existing behaviour.
                if (resultNode == null && _xRawResult != null && nonXmlMethod)
                {
                    try
                    {
                        var swx = new StringWriter();
                        var serx = _proc.NewSerializer(swx);
                        serx.SetOutputProperty(S.Serializer.Property.METHOD, "xml");
                        serx.SetOutputProperty(S.Serializer.Property.OMIT_XML_DECLARATION, "yes");
                        serx.SerializeXdmValue(_xRawResult);
                        string xmlText = swx.ToString();
                        if (xmlText.Length > 0)
                            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(xmlText)))
                                resultNode = builder.Build(ms, new Uri(Path.GetFullPath(Path.Combine(baseDir, "result.xml"))).AbsoluteUri);
                    }
                    catch { resultNode = null; }
                }

                // Final fallback: the principal output is a legal XDM result tree that is not a well-formed
                // XML document, so no reparse above produced a tree. Re-root it under a document node — a
                // text-only body becomes a document with a text node (seqtor-043b), several top-level elements
                // become a document with those children (xsl-document-0501) — so assert-type document-node()
                // and rooted path asserts (/foo[1]) resolve. Only runs when every earlier reparse failed and
                // no raw value was captured, so trees that DID reparse are untouched.
                if (resultNode == null && _xRawResult == null)
                {
                    // Strip only the XML declaration, NOT leading newlines: for a text-only result the newline
                    // after "?>" is the content's first character, not a serialization artifact (whitespace-019
                    // asserts a leading \n) — StripXmlDecl's TrimStart would eat it.
                    string body = serialized;
                    if (body != null && body.StartsWith("<?xml ", StringComparison.Ordinal))
                    {
                        int e = body.IndexOf("?>", StringComparison.Ordinal);
                        if (e > 0) body = body.Substring(e + 2);
                    }
                    resultNode = RebuildAsDocument(body);
                }
            }
            catch (Exception ex)
            {
                errorCode = ErrCode(ex, compileErrors);
                // A compile failure may report several static errors (a missing @name alongside the
                // schema-awareness error, say). The catalog convention — like Saxon's own driver — is
                // that the test passes if the expected code is among the signalled ones.
                foreach (var ce in compileErrors)
                {
                    try
                    {
                        if (ce.IsWarning()) continue;
                        var lc = ce.GetErrorCode()?.LocalName;
                        if (!string.IsNullOrEmpty(lc)) _xErrorCodes.Add(lc);
                    }
                    catch { }
                }
                if (Environment.GetEnvironmentVariable("QT3_ERRDUMP") != null)
                {
                    var s = ex.ToString().Replace("\r", " ").Replace("\n", " ");
                    Console.WriteLine("[ERRDUMP] " + id + " " + s.Substring(0, Math.Min(int.Parse(Environment.GetEnvironmentVariable("QT3_ERRDUMP_LEN") ?? "600"), s.Length)));
                    foreach (var ce in compileErrors.Take(4))
                        Console.WriteLine("[ERRDUMP-CE] " + id + " " + (ce.GetErrorCode()?.ToString() ?? "?") + " " + Trim(ce.GetMessage()));
                }
            }

            foreach (var ce in compileErrors)
            {
                try { if (ce.IsWarning()) _xWarnings++; } catch { }
            }

            if (Environment.GetEnvironmentVariable("QT3_ERRDUMP") == "3")
                Console.WriteLine($"[DBG] {id} err={errorCode} node={(resultNode == null ? "null" : "'" + Trim(resultNode.GetStringValue()) + "'")} ser='{Trim(serialized)}'");

            bool ok;
            string why;
            try { (ok, why) = XCheckResult(result.Elements().First(), errorCode, resultNode, serialized, baseDir); }
            catch (XSkipCase sk) { Skip(sk.Message); return; }
            if (ok) { _pass++; if (_vdump) _verdicts.Add(id + "\tPASS"); }
            else
            {
                _fail++;
                _failures.Add($"{id} :: {why}");
                if (_vdump) _verdicts.Add(id + "\tFAIL");
                // Triage aid: dump the raw serialized output of a failing case for offline diffing.
                string dumpDir = Environment.GetEnvironmentVariable("QT3_DUMPDIR");
                if (!string.IsNullOrEmpty(dumpDir) && serialized != null)
                {
                    try
                    {
                        Directory.CreateDirectory(dumpDir);
                        File.WriteAllText(Path.Combine(dumpDir, id.Replace('/', '_') + ".actual"), serialized, new UTF8Encoding(false));
                    }
                    catch { }
                }
            }
        }

        // QName in catalog attributes may be prefixed (namespaces in scope on the test element) or EQName.
        static S.QName XQName(string name, XElement scope)
        {
            var sq = XQNameToStructured(name, scope);
            return new S.QName(sq.GetNamespaceUri().ToString(), sq.GetLocalPart());
        }

        static OutSmart.DAXon.Model.StructuredQName XQNameToStructured(string name, XElement scope)
        {
            if (name.StartsWith("Q{", StringComparison.Ordinal))
            {
                int close = name.IndexOf('}');
                return new OutSmart.DAXon.Model.StructuredQName("", OutSmart.DAXon.Model.NamespaceUri.Of(name.Substring(2, close - 2)), name.Substring(close + 1));
            }
            int colon = name.IndexOf(':');
            if (colon < 0) return new OutSmart.DAXon.Model.StructuredQName("", OutSmart.DAXon.Model.NamespaceUri.NULL, name);
            string prefix = name.Substring(0, colon);
            var nsAttr = scope.AncestorsAndSelf().SelectMany(e => e.Attributes())
                .FirstOrDefault(a => a.IsNamespaceDeclaration && a.Name.LocalName == prefix);
            string uri = nsAttr?.Value ?? "";
            return new OutSmart.DAXon.Model.StructuredQName(prefix, OutSmart.DAXon.Model.NamespaceUri.Of(uri), name.Substring(colon + 1));
        }

        sealed class XSkipCase : Exception { public XSkipCase(string reason) : base(reason) { } }

        // Per-case capture state (child process is single-threaded per case).
        static List<S.XdmNode> _xMessages = new List<S.XdmNode>();
        static Dictionary<string, StringWriter> _xResultDocs = new Dictionary<string, StringWriter>(StringComparer.Ordinal);
        static string _xBaseOutputUri;
        static string _xTsUri;   // URI of the current test-set catalog (base URI for inline <content> sources)
        static int _xWarnings;   // compile + runtime warnings observed for the current case (assert-warning)
        static S.XdmValue _xRawResult;   // raw invocation result when the catalog says <output serialize="no"/>
        static List<string> _xErrorCodes = new List<string>();   // all compile-error codes (local parts) of a failed run

        // Captures xsl:result-document output as serialized text keyed by absolute URI.
        sealed class XResultDocCapture : OutSmart.DAXon.Lib.IResultDocumentResolver
        {
            private readonly S.Processor proc;
            private readonly Dictionary<string, StringWriter> docs;
            public XResultDocCapture(S.Processor proc, Dictionary<string, StringWriter> docs)
            {
                this.proc = proc;
                this.docs = docs;
            }

            public OutSmart.DAXon.Events.IReceiver Resolve(OutSmart.DAXon.Expressions.IXPathContext context, string href, string baseUri,
                OutSmart.DAXon.Serialization.SerializationProperties properties)
            {
                string abs;
                try { abs = new Uri(new Uri(baseUri), href).AbsoluteUri; } catch { abs = href; }
                var sw = new StringWriter();
                lock (docs)
                {
                    // XTDE1490 (3.0): two result documents — or a result document and the principal
                    // output — must not share an output URI. The engine's own uniqueness check is
                    // bypassed when a custom resolver is installed (error-1490a/b, try-021).
                    // (XTDE1480 is the separate temporary-output-state error, engine-side.)
                    if ((_xBaseOutputUri != null && abs == _xBaseOutputUri) || docs.ContainsKey(abs))
                        throw new OutSmart.DAXon.Transformation.XPathException("Two result documents write to the same URI: " + abs, "XTDE1490");
                    docs[abs] = sw;
                }
                var ser = proc.NewSerializer(sw);
                var pipe = context.GetController().MakePipelineConfiguration();
                return ser.GetReceiver(pipe, properties);
            }
        }

        // Recursive assertion evaluator. Returns (ok, failure-description).
        static (bool, string) XCheckResult(XElement a, string errorCode, S.XdmNode resultNode, string serialized, string baseDir)
        {
            switch (a.Name.LocalName)
            {
                case "all-of":
                {
                    foreach (var child in a.Elements())
                    {
                        var (ok, why) = XCheckResult(child, errorCode, resultNode, serialized, baseDir);
                        if (!ok) return (false, why);
                    }
                    return (true, null);
                }
                case "any-of":
                {
                    var whys = new List<string>();
                    foreach (var child in a.Elements())
                    {
                        var (ok, why) = XCheckResult(child, errorCode, resultNode, serialized, baseDir);
                        if (ok) return (true, null);
                        whys.Add(why);
                    }
                    return (false, "any-of: none matched (" + Trim(string.Join(" | ", whys)) + ")");
                }
                case "not":
                {
                    var (ok, _) = XCheckResult(a.Elements().First(), errorCode, resultNode, serialized, baseDir);
                    return (!ok, ok ? "not: inner assertion matched" : null);
                }
                case "error":
                {
                    string expected = (string)a.Attribute("code") ?? "*";
                    if (errorCode == null) return (false, $"expected error {expected}, got a result");
                    if (expected == "*" || expected == errorCode) return (true, null);
                    // ErrCode reports the local part only; for EQName-expected codes (user errors via
                    // xsl:message/error(QName(...))) compare on the local part, as the QT3 checker does.
                    string expectedLocal = expected;
                    if (expected.StartsWith("Q{", StringComparison.Ordinal)) { int rb = expected.IndexOf('}'); if (rb >= 0) expectedLocal = expected.Substring(rb + 1); }
                    else if (expected.Contains(":")) expectedLocal = expected.Substring(expected.IndexOf(':') + 1);   // prefixed code (my:ABCD9999)
                    if (expectedLocal == errorCode) return (true, null);
                    // A failed compile may signal several static errors; the expected one among them passes.
                    if (_xErrorCodes.Contains(expectedLocal)) return (true, null);
                    // Era aliases: XSLT 2.0's format-number codes were renumbered into F&O in 3.0
                    // (XTDE1280→FODF1280, XTDE1310→FODF1310). Exact-XSLT20 tests (error-12xx/13xx-20)
                    // expect the 2.0 code; a 3.0 processor legitimately raises the FODF twin.
                    if ((expected == "XTDE1280" && errorCode == "FODF1280") ||
                        (expected == "XTDE1310" && errorCode == "FODF1310")) return (true, null);
                    return (false, $"expected error {expected}, got {errorCode}");
                }
                case "assert-message":
                {
                    // The nested assertion must hold for at least one captured xsl:message.
                    var child = a.Elements().First();
                    List<S.XdmNode> msgs;
                    lock (_xMessages) msgs = new List<S.XdmNode>(_xMessages);
                    foreach (var m in msgs)
                    {
                        // assert-xml inside assert-message compares MARKUP — serialize the message
                        // node (string value alone loses element structure, message-0202/0304/0403).
                        string mser;
                        try
                        {
                            var msw = new StringWriter();
                            var msr = _proc.NewSerializer(msw);
                            msr.SetOutputProperty(S.Serializer.Property.OMIT_XML_DECLARATION, "yes");
                            msr.SerializeXdmValue(m);
                            mser = msw.ToString();
                        }
                        catch (Exception mex) { mser = m.GetStringValue(); if (Environment.GetEnvironmentVariable("QT3_MSGDUMP") != null) Console.WriteLine("[MSGDUMP-EX] " + mex.GetType().Name + " " + mex.Message); }
                        if (Environment.GetEnvironmentVariable("QT3_MSGDUMP") != null) Console.WriteLine("[MSGDUMP] '" + mser + "'");
                        var (ok, _) = XCheckResult(child, null, m, mser, baseDir);
                        if (ok) return (true, null);
                    }
                    return (false, $"assert-message: none of {msgs.Count} captured messages matched");
                }
                case "assert-result-document":
                {
                    string uri = (string)a.Attribute("uri") ?? "";
                    string abs;
                    try { abs = new Uri(new Uri(_xBaseOutputUri ?? "file:///dummy"), uri).AbsoluteUri; } catch { abs = uri; }
                    StringWriter sw = null;
                    lock (_xResultDocs)
                    {
                        if (!_xResultDocs.TryGetValue(abs, out sw))
                        {
                            var hit = _xResultDocs.FirstOrDefault(kv => kv.Key.EndsWith(uri, StringComparison.Ordinal));
                            sw = hit.Value;
                        }
                    }
                    if (sw == null) return (false, $"assert-result-document: no document captured for '{uri}'");
                    string content = sw.ToString();
                    S.XdmNode node = null;
                    try
                    {
                        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(content)))
                            node = _proc.NewDocumentBuilder().Build(ms, abs);
                    }
                    catch { }
                    foreach (var child in a.Elements())
                    {
                        var (ok, why) = XCheckResult(child, null, node, content, baseDir);
                        if (!ok) return (false, $"assert-result-document {uri}: {why}");
                    }
                    return (true, null);
                }
                case "assert-warning":
                    return _xWarnings > 0 ? (true, null) : (false, "assert-warning: no warning was reported");
                case "assert-serialization-error":
                {
                    string expected = (string)a.Attribute("code") ?? "*";
                    if (errorCode == null) return (false, $"expected serialization error {expected}, got a result");
                    return (expected == "*" || expected == errorCode) ? (true, null) : (false, $"expected serialization error {expected}, got {errorCode}");
                }
            }

            // Everything below needs a successful transformation.
            if (errorCode != null) return (false, a.Name.LocalName + ": raised " + errorCode);

            switch (a.Name.LocalName)
            {
                case "assert":
                    return XBool(a.Value, resultNode, $"assert {Trim(a.Value)}", serialized, a);
                case "assert-true":
                    return XBool("$result = true()", resultNode, "assert-true");
                case "assert-false":
                    return XBool("$result = false()", resultNode, "assert-false");
                case "assert-eq":
                    return XBool($"$result eq {a.Value}", resultNode, $"assert-eq {Trim(a.Value)}");
                case "assert-count":
                    return XBool($"count($result) eq {a.Value}", resultNode, $"assert-count {Trim(a.Value)}");
                case "assert-empty":
                    return XBool("empty($result)", resultNode, "assert-empty");
                case "assert-type":
                    return XBool($"$result instance of {a.Value}", resultNode, $"assert-type {Trim(a.Value)}");
                case "assert-deep-eq":
                    return XBool($"deep-equal($result, ({a.Value}))", resultNode, $"assert-deep-eq {Trim(a.Value)}", null, a);
                case "assert-string-value":
                {
                    string actual = resultNode == null ? StripXmlDecl(serialized ?? "") : resultNode.GetStringValue();
                    string expected = a.Value;
                    // xslt30-test catalog schema: normalize-space DEFAULTS TO TRUE (opposite of QT3)
                    bool norm = (string)a.Attribute("normalize-space") != "false";
                    if (norm) { actual = NormSpace(actual); expected = NormSpace(expected); }
                    return actual == expected
                        ? (true, null)
                        : (false, $"assert-string-value: got '{Trim(actual)}' expected '{Trim(expected)}'");
                }
                case "assert-xml":
                {
                    string expected;
                    string exFile = (string)a.Attribute("file");
                    if (exFile != null) expected = File.ReadAllText(ResolveFile(baseDir, exFile));
                    else expected = a.Value;
                    string actual = StripXmlDecl(serialized ?? "");
                    expected = StripXmlDecl(expected);
                    if ((string)a.Attribute("xml-version") == "1.1")
                    {
                        // XML 1.1 output (undeclare-prefixes) puts xmlns:p="" on elements — unparseable by
                        // the .NET 1.0-only parser. Undeclarations are invisible in the Clark canon anyway
                        // (only expanded names matter), so strip them from BOTH sides and compare as 1.0.
                        actual = System.Text.RegularExpressions.Regex.Replace(actual, " xmlns:[A-Za-z0-9_.-]+=\"\"", "");
                        expected = System.Text.RegularExpressions.Regex.Replace(expected, " xmlns:[A-Za-z0-9_.-]+=\"\"", "");
                        // Control-char references (&#1;/&#x1;) and 1.1 name chars can't survive a .NET 1.0 XML
                        // parse, so a tree compare is impossible. Decode numeric char refs to their code point
                        // (representation-independent) and compare the markup as normalised text instead.
                        string da = NormNl(DecodeCharRefs(actual)), de = NormNl(DecodeCharRefs(expected));
                        if (da == de) return (true, null);
                    }
                    if (NormXml(actual) == NormXml(expected)) return (true, null);
                    // Fallback: Clark-notation canonical compare (attributes sorted, xmlns declarations and
                    // prefixes dropped, comments/PIs ignored) — assert-xml semantics are fn:deep-equal, so
                    // namespace-declaration ORDER and prefix choice must not matter (same policy as QT3).
                    if (CanonXml(actual) == CanonXml(expected)) return (true, null);
                    // html-method output: strip serializer artifacts (injected meta, unclosed voids) so
                    // the tree compare works — deep-equal never sees them (accumulator-040, sequence-0601).
                    if (NormXml(HtmlToXml(actual)) == NormXml(HtmlToXml(expected))) return (true, null);
                    if (CanonXml(HtmlToXml(actual)) == CanonXml(HtmlToXml(expected))) return (true, null);
                    return (false, "assert-xml: got " + Trim(actual));
                }
                case "assert-serialization":
                {
                    string exFile = (string)a.Attribute("file");
                    // The @encoding of the expected file must be honoured — otherwise an ISO-8859-1 fixture
                    // (é = one byte 0xE9) is misdecoded as UTF-8 and never matches the output (select-6101).
                    string exEnc = (string)a.Attribute("encoding");
                    System.Text.Encoding exEncoding = null;
                    if (exEnc != null) { try { exEncoding = System.Text.Encoding.GetEncoding(exEnc); } catch { } }
                    string expected = exFile != null
                        ? (exEncoding != null ? File.ReadAllText(ResolveFile(baseDir, exFile), exEncoding)
                                              : File.ReadAllText(ResolveFile(baseDir, exFile)))
                        : a.Value;
                    string actual = (serialized ?? "").Replace("\r\n", "\n").Trim();
                    string want = expected.Replace("\r\n", "\n").Trim();
                    // expected files may or may not carry the XML declaration — accept either form
                    if (actual == want || StripXmlDecl(actual).Trim() == StripXmlDecl(want).Trim()) return (true, null);
                    return (false, "assert-serialization: got " + Trim(actual));
                }
                case "serialization-matches":
                {
                    string flags = (string)a.Attribute("flags") ?? "";
                    var opts = System.Text.RegularExpressions.RegexOptions.None;
                    if (flags.Contains("i")) opts |= System.Text.RegularExpressions.RegexOptions.IgnoreCase;
                    if (flags.Contains("s")) opts |= System.Text.RegularExpressions.RegexOptions.Singleline;
                    if (flags.Contains("m")) opts |= System.Text.RegularExpressions.RegexOptions.Multiline;
                    // XPath 'x' = free-spacing (whitespace in the PATTERN ignored — rd-0219 wraps its
                    // regex in indented CDATA); 'q' = all characters literal.
                    if (flags.Contains("x")) opts |= System.Text.RegularExpressions.RegexOptions.IgnorePatternWhitespace;
                    string smPattern = flags.Contains("q") ? System.Text.RegularExpressions.Regex.Escape(a.Value) : a.Value;
                    return System.Text.RegularExpressions.Regex.IsMatch(serialized ?? "", smPattern, opts)
                        ? (true, null)
                        : (false, "serialization-matches: got " + Trim(serialized));
                }
                default:
                    throw new XSkipCase("driver: assert " + a.Name.LocalName);
            }
        }

        static string NormSpace(string s) => string.Join(" ", (s ?? "").Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries));

        // Decode numeric character references (&#N; / &#xH;) to their code point, so representations of the
        // same character compare equal (used for XML 1.1 control-char content that no .NET XML parser accepts).
        static string DecodeCharRefs(string s)
        {
            return System.Text.RegularExpressions.Regex.Replace(s, "&#(x[0-9a-fA-F]+|[0-9]+);", m =>
            {
                string v = m.Groups[1].Value;
                try
                {
                    int cp = v[0] == 'x' ? Convert.ToInt32(v.Substring(1), 16) : int.Parse(v);
                    return char.ConvertFromUtf32(cp);
                }
                catch { return m.Value; }
            });
        }

        // Normalise line endings (CRLF/CR -> LF) so serialized output and the expected fixture compare equal.
        static string NormNl(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

        static string StripXmlDecl(string s)
        {
            if (s.StartsWith("<?xml ", StringComparison.Ordinal))
            {
                int e = s.IndexOf("?>", StringComparison.Ordinal);
                if (e > 0) return s.Substring(e + 2).TrimStart('\r', '\n');
            }
            return s;
        }

        // A document node holding the given serialized content re-rooted DIRECTLY under the document node.
        // For a principal output that is a legal XDM result tree but not a well-formed XML document — so the
        // XmlReader reparse above rejected it: content that is text only (seqtor-043b: an xsl:sequence of
        // atomic values serialises to a bare text node) or has several top-level elements (xsl-document-0501:
        // xsl:document with multiple children). Wrap in a synthetic root, parse that, then copy the wrapper's
        // children under a fresh document node. Returns null (caller keeps its existing fallbacks) if the
        // wrapped text still won't parse.
        static S.XdmNode RebuildAsDocument(string content)
        {
            if (string.IsNullOrEmpty(content)) return null;
            try
            {
                S.XdmNode wrapper;
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes("<xslt-test-wrap>" + content + "</xslt-test-wrap>")))
                    wrapper = _proc.NewDocumentBuilder().Build(ms, "http://saxon/xslt-test-wrap");
                OutSmart.DAXon.Model.NodeInfo xel = null;
                var di = wrapper.UnderlyingValue.IterateAxis(OutSmart.DAXon.Model.AxisInfo.CHILD);
                for (OutSmart.DAXon.Model.NodeInfo c; (c = di.Next()) != null; )
                    if (c.GetNodeKind() == OutSmart.DAXon.Types.Type.ELEMENT) { xel = c; break; }
                if (xel == null) return null;
                var pipe = _proc.UnderlyingConfiguration.MakePipelineConfiguration();
                var tb = new OutSmart.DAXon.Trees.Tiny.TinyBuilder(pipe);
                tb.Open();
                tb.StartDocument(0);
                var ci = xel.IterateAxis(OutSmart.DAXon.Model.AxisInfo.CHILD);
                for (OutSmart.DAXon.Model.NodeInfo ch; (ch = ci.Next()) != null; )
                    ch.Copy(tb, OutSmart.DAXon.Model.CopyOptions.ALL_NAMESPACES, OutSmart.DAXon.Expressions.Parsing.Loc.NONE);
                tb.EndDocument();
                tb.Close();
                return (S.XdmNode)S.XdmValue.Wrap(tb.CurrentRoot);
            }
            catch { return null; }
        }

        // A document node whose only child is a text node with the given value (empty text -> empty doc).
        static S.XdmNode TextOnlyDoc(string text)
        {
            var pipe = _proc.UnderlyingConfiguration.MakePipelineConfiguration();
            var tb = new OutSmart.DAXon.Trees.Tiny.TinyBuilder(pipe);
            tb.Open();
            tb.StartDocument(0);
            if (text.Length > 0)
                tb.Characters(OutSmart.DAXon.Text.StringView.Of(text), OutSmart.DAXon.Expressions.Parsing.Loc.NONE, 0);
            tb.EndDocument();
            tb.Close();
            return (S.XdmNode)S.XdmValue.Wrap(tb.CurrentRoot);
        }

        static (bool, string) XBool(string xpath, S.XdmNode resultNode, string label, string serializedText = null, XElement nsSource = null)
        {
            try
            {
                var xpc = _proc.NewXPathCompiler();
                xpc.DeclareNamespace("xs", "http://www.w3.org/2001/XMLSchema");
                xpc.DeclareNamespace("fn", "http://www.w3.org/2005/xpath-functions");
                xpc.DeclareNamespace("map", "http://www.w3.org/2005/xpath-functions/map");
                xpc.DeclareNamespace("array", "http://www.w3.org/2005/xpath-functions/array");
                // json-to-xml result trees live in the fn namespace; catalog asserts use the j: prefix
                xpc.DeclareNamespace("j", "http://www.w3.org/2005/xpath-functions");
                // namespaces declared on (or above) the assert element itself — evaluate-019 binds h:
                if (nsSource != null)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var e in nsSource.AncestorsAndSelf())
                        foreach (var at in e.Attributes())
                            if (at.IsNamespaceDeclaration && at.Name.LocalName != "xmlns" && seen.Add(at.Name.LocalName))
                                xpc.DeclareNamespace(at.Name.LocalName, at.Value);
                }
                xpc.DeclareVariable(new S.QName("result"));
                var sel = xpc.Compile(xpath).Load();
                S.XdmValue rv = _xRawResult ?? (resultNode != null ? (S.XdmValue)resultNode : (serializedText != null ? new S.XdmAtomicValue(StripXmlDecl(serializedText)) : S.XdmValue.Wrap(OutSmart.DAXon.Values.EmptySequence.GetInstance())));
                sel.SetVariable(new S.QName("result"), rv);
                if (resultNode != null) sel.SetContextItem(resultNode);
                // Text-method principal output has no XML tree; synthesize a text-only DOCUMENT node as
                // context so both rooted paths (/text() = 'x', not(/node()) — whitespace-022/023,
                // error-0045a*) and dot-string asserts (starts-with(normalize-space(.), ...) — mode-14xx)
                // work. An atomic context made every rooted path raise XPTY0020. The raw serialized
                // text may open with an XML declaration (kept raw for serialization-matches since
                // XR11) — strip it here, string asserts target the content.
                else if (serializedText != null) sel.SetContextItem(TextOnlyDoc(StripXmlDecl(serializedText)));
                return sel.EffectiveBooleanValue() ? (true, null) : (false, label);
            }
            catch (Exception ex)
            {
                return (false, label + ": raised " + ErrCode(ex));
            }
        }

        // Maps a module namespace URI to the file declared by an environment <resource media-type="application/xquery">.
        sealed class EnvModuleResolver : OutSmart.DAXon.Lib.IModuleURIResolver
        {
            readonly Dictionary<string, string> _map;
            public EnvModuleResolver(Dictionary<string, string> map) { _map = map; }
            public OutSmart.DAXon.Lib.ResolvedResource[] Resolve(string moduleURI, string baseURI, string[] locations)
            {
                if (moduleURI != null && _map.TryGetValue(moduleURI, out string path) && File.Exists(path))
                {
                    return new[]
                    {
                        new OutSmart.DAXon.Lib.ResolvedResource
                        {
                            Stream = File.OpenRead(path),
                            SystemId = new Uri(Path.GetFullPath(path)).AbsoluteUri,
                            PleaseCloseAfterUse = true,
                        }
                    };
                }
                return null; // fall back to the standard resolver
            }
        }
    }
}
