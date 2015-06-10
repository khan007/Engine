﻿using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using R4nd0mApps.TddStud10.Common.Domain;
using R4nd0mApps.TddStud10.Engine.Diagnostics;

namespace R4nd0mApps.TddStud10
{
    internal static class Instrumentation
    {
        public static PerDocumentSequencePoints GenerateSequencePointInfo(IRunExecutorHost host, DateTime timeFilter, string buildOutputRoot)
        {
            try
            {
                return GenerateSequencePointInfoImpl(host, timeFilter, buildOutputRoot);
            }
            catch (Exception e)
            {
                Logger.I.LogError("Failed to instrument. Exception: {0}", e);
            }

            return null;
        }

        public static PerDocumentSequencePoints GenerateSequencePointInfoImpl(IRunExecutorHost host, DateTime timeFilter, string buildOutputRoot)
        {
            Logger.I.LogInfo(
                "Generating sequence point info: Time filter - {0}, Build output root - {1}.",
                timeFilter.ToLocalTime(),
                buildOutputRoot);

            var perDocSP = new PerDocumentSequencePoints();
            Engine.Engine.FindAndExecuteForEachAssembly(
                host,
                buildOutputRoot,
                timeFilter,
                (string assemblyPath) =>
                {
                    Logger.I.LogInfo("Generating sequence point info for {0}.", assemblyPath);

                    var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters { ReadSymbols = true });

                    var sps = from mod in assembly.Modules
                              from t in mod.GetTypes()
                              from m in t.Methods
                              where m.Body != null && m.Body.Instructions.Count != 0
                              from i in m.Body.Instructions
                              where i.SequencePoint != null
                              where i.SequencePoint.StartLine != 0xfeefee
                              select new { mod, m, i.SequencePoint };

                    int id = 0;
                    foreach (var sp in sps)
                    {
                        var fp = FilePath.NewFilePath(sp.SequencePoint.Document.Url);
                        var seqPts = perDocSP.GetOrAdd(fp, _ => new ConcurrentBag<R4nd0mApps.TddStud10.Common.Domain.SequencePoint>());

                        seqPts.Add(new R4nd0mApps.TddStud10.Common.Domain.SequencePoint
                        {
                            methodId = new MethodId(AssemblyId.NewAssemblyId(sp.mod.Mvid), MdTokenRid.NewMdTokenRid(sp.m.MetadataToken.RID)),
                            id = SequencePointId.NewSequencePointId(id++),
                            document = fp,
                            startLine = DocumentCoordinate.NewDocumentCoordinate(sp.SequencePoint.StartLine),
                            startColumn = DocumentCoordinate.NewDocumentCoordinate(sp.SequencePoint.StartColumn),
                            endLine = DocumentCoordinate.NewDocumentCoordinate(sp.SequencePoint.EndLine),
                            endColumn = DocumentCoordinate.NewDocumentCoordinate(sp.SequencePoint.EndColumn),
                        });
                    }
                });

            return perDocSP;
        }

        public static void Instrument(IRunExecutorHost host, DateTime timeFilter, string solutionRoot, string solutionSnapshotRoot, string buildOutputRoot, PerAssemblyTestCases testsPerAssembly)
        {
            try
            {
                InstrumentImpl(host, timeFilter, solutionRoot, solutionSnapshotRoot, buildOutputRoot, testsPerAssembly);
            }
            catch (Exception e)
            {
                Logger.I.LogError("Failed to instrument. Exception: {0}", e);
            }
        }

        public static void InstrumentImpl(IRunExecutorHost host, DateTime timeFilter, string solutionSnapshotRoot, string solutionRoot, string buildOutputRoot, PerAssemblyTestCases testsPerAssembly)
        {
            Logger.I.LogInfo(
                "Instrumenting: Time filter - {0}, Build output root - {1}.",
                timeFilter.ToLocalTime(),
                buildOutputRoot);

            System.Reflection.StrongNameKeyPair snKeyPair = null;
            var snKeyFile = Directory.EnumerateFiles(solutionRoot, "*.snk").FirstOrDefault();
            if (snKeyFile != null)
            {
                snKeyPair = new System.Reflection.StrongNameKeyPair(File.ReadAllBytes(snKeyFile));
                Logger.I.LogInfo("Using strong name from {0}.", snKeyFile);
            }

            var asmResolver = new DefaultAssemblyResolver();
            Array.ForEach(asmResolver.GetSearchDirectories(), asmResolver.RemoveSearchDirectory);
            asmResolver.AddSearchDirectory(buildOutputRoot);
            var readerParams = new ReaderParameters
            {
                AssemblyResolver = asmResolver,
                ReadSymbols = true,
            };

            string testRunnerPath = Path.GetFullPath(typeof(R4nd0mApps.TddStud10.TestRuntime.Marker).Assembly.Location);
            var enterSPMD = from t in ModuleDefinition.ReadModule(testRunnerPath).GetTypes()
                            where t.Name == "Marker"
                            from m in t.Methods
                            where m.Name == "EnterSequencePoint"
                            select m;

            var exitUTMD = from t in ModuleDefinition.ReadModule(testRunnerPath).GetTypes()
                           where t.Name == "Marker"
                           from m in t.Methods
                           where m.Name == "ExitUnitTest"
                           select m;

            Func<string, string> rebaseDocument = s => s.ToUpperInvariant().Replace(solutionSnapshotRoot.ToUpperInvariant(), solutionRoot.ToUpperInvariant());

            Engine.Engine.FindAndExecuteForEachAssembly(
                host,
                buildOutputRoot,
                timeFilter,
                (string assemblyPath) =>
                {
                    Logger.I.LogInfo("Instrumenting {0}.", assemblyPath);

                    var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, readerParams);

                    /*
                       IL_0001: ldstr <assemblyId>
                       IL_0006: ldstr <mdtoken>
                       IL_000b: ldstr <spid>
                       IL_000d: call void R4nd0mApps.TddStud10.TestHost.Marker::ExitUnitTest(string, ldstr, ldstr)
                     */
                    MethodReference enterSPMR = assembly.MainModule.Import(enterSPMD.First());
                    MethodReference exitUTMR = assembly.MainModule.Import(exitUTMD.First());

                    foreach (var module in assembly.Modules)
                    {
                        foreach (var type in module.Types)
                            foreach (MethodDefinition meth in type.Methods)
                            {
                                if (meth.Body == null || meth.Body.Instructions.Count <= 0)
                                {
                                    continue;
                                }

                                meth.Body.SimplifyMacros();

                                var spi = from i in meth.Body.Instructions
                                          where i.SequencePoint != null
                                          where i.SequencePoint.StartLine != 0xfeefee
                                          select i;

                                var spId = 0;
                                var instructions = spi.ToArray();
                                foreach (var sp in instructions)
                                {
                                    /**********************************************************************************/
                                    /*                                PDB Path Replace                                */
                                    /**********************************************************************************/
                                    sp.SequencePoint.Document.Url = rebaseDocument(sp.SequencePoint.Document.Url);

                                    /**********************************************************************************/
                                    /*                            Inject Enter Sequence Point                         */
                                    /**********************************************************************************/
                                    Instruction instrMarker = sp;
                                    Instruction instr = null;
                                    var ilProcessor = meth.Body.GetILProcessor();

                                    // IL_000d: call void R4nd0mApps.TddStud10.TestHost.Marker::EnterSequencePoint(string, ldstr, ldstr)
                                    instr = ilProcessor.Create(OpCodes.Call, enterSPMR);
                                    ilProcessor.InsertBefore(instrMarker, instr);
                                    instrMarker = instr;
                                    // IL_000b: ldstr <spid>
                                    instr = ilProcessor.Create(OpCodes.Ldstr, (spId++).ToString());
                                    ilProcessor.InsertBefore(instrMarker, instr);
                                    instrMarker = instr;
                                    // IL_0006: ldstr <mdtoken>
                                    instr = ilProcessor.Create(OpCodes.Ldstr, meth.MetadataToken.RID.ToString());
                                    ilProcessor.InsertBefore(instrMarker, instr);
                                    instrMarker = instr;
                                    // IL_0001: ldstr <assemblyId>
                                    instr = ilProcessor.Create(OpCodes.Ldstr, module.Mvid.ToString());
                                    ilProcessor.InsertBefore(instrMarker, instr);
                                    instrMarker = instr;
                                }

                                /*************************************************************************************/
                                /*                            Inject Exit Unit Test                                  */
                                /*************************************************************************************/
                                var ret = IsSequencePointAtStartOfAUnitTest(spi.Select(i => i.SequencePoint).FirstOrDefault(), FilePath.NewFilePath(assemblyPath), testsPerAssembly);
                                if (ret.Item1)
                                {
                                    if (!meth.IsConstructor && meth.ReturnType == module.TypeSystem.Void && !meth.IsAsync())
                                    {
                                        InjectExitUtCallInsideMethodWiseFinally(module, meth, ret.Item2, exitUTMR);
                                    }
                                    else
                                    {
                                        Logger.I.LogError("Instrumentation: Unsupported method type: IsConstructo = {0}, Return Type = {1}, IsAsync = {2}.", meth.IsConstructor, meth.ReturnType, meth.IsAsync());
                                    }
                                }

                                meth.Body.InitLocals = true;
                                meth.Body.OptimizeMacros();
                            }
                    }

                    var backupAssemblyPath = Path.ChangeExtension(assemblyPath, ".original");
                    File.Delete(backupAssemblyPath);
                    File.Move(assemblyPath, backupAssemblyPath);
                    try
                    {
                        assembly.Write(assemblyPath, new WriterParameters { WriteSymbols = true, StrongNameKeyPair = snKeyPair });
                    }
                    catch
                    {
                        Logger.I.LogInfo("Backing up or instrumentation failed. Attempting to revert back changes to {0}.", assemblyPath);
                        File.Delete(assemblyPath);
                        File.Move(backupAssemblyPath, assemblyPath);
                        throw;
                    }

                },
                1);
        }

        private static void InjectExitUtCallInsideMethodWiseFinally(
           ModuleDefinition mod,
           MethodDefinition meth, TestId testId, MethodReference exitMarkerMethodRef)
        {
            ILProcessor ilProcessor = meth.Body.GetILProcessor();

            var firstInstruction = FindFirstInstructionSkipCtor(meth);
            Instruction returnInstruction = FixReturns(meth, mod);

            var beforeReturn = Instruction.Create(OpCodes.Endfinally);
            ilProcessor.InsertBefore(returnInstruction, beforeReturn);

            /////////////// Start of try block  
            Instruction nopInstruction1 = Instruction.Create(OpCodes.Nop);
            ilProcessor.InsertBefore(firstInstruction, nopInstruction1);

            //////// Start Finally block
            Instruction nopInstruction2 = Instruction.Create(OpCodes.Nop);
            ilProcessor.InsertBefore(beforeReturn, nopInstruction2);

            Instruction instrMarker = nopInstruction2;
            Instruction instr = null;

            // IL_000d: call void R4nd0mApps.TddStud10.TestHost.Marker::EnterUnitTest(ldstr)
            instr = ilProcessor.Create(OpCodes.Call, exitMarkerMethodRef);
            ilProcessor.InsertBefore(instrMarker, instr);
            instrMarker = instr;
            // IL_0006: ldstr <string>
            instr = ilProcessor.Create(OpCodes.Ldstr, testId.line.Item.ToString(CultureInfo.InvariantCulture));
            ilProcessor.InsertBefore(instrMarker, instr);
            instrMarker = instr;
            // IL_0006: ldstr <string>
            instr = ilProcessor.Create(OpCodes.Ldstr, testId.document.Item);
            ilProcessor.InsertBefore(instrMarker, instr);
            instrMarker = instr;
            // IL_0006: ldstr <string>
            instr = ilProcessor.Create(OpCodes.Ldstr, testId.source.Item);
            ilProcessor.InsertBefore(instrMarker, instr);
            instrMarker = instr;
            ///////// End finally block

            var handler = new ExceptionHandler(ExceptionHandlerType.Finally)
            {
                TryStart = nopInstruction1,
                TryEnd = instrMarker,
                HandlerStart = instrMarker,
                HandlerEnd = returnInstruction,
            };

            meth.Body.ExceptionHandlers.Add(handler);
        }

        /// <nn />
        private static Instruction FindFirstInstructionSkipCtor(MethodDefinition med)
        {
            MethodBody body = med.Body;
            if (med.IsConstructor && !med.IsStatic)
            {
                return body.Instructions.Skip(2).First();
            }

            return body.Instructions.First();
        }

        /// <nn />
        private static Instruction FixReturns(MethodDefinition med, ModuleDefinition mod)
        {
            MethodBody body = med.Body;

            Instruction formallyLastInstruction = body.Instructions.Last();
            Instruction lastLeaveInstruction = null;
            if (med.ReturnType == mod.TypeSystem.Void)
            {
                var instructions = body.Instructions;
                var lastRet = Instruction.Create(OpCodes.Ret);
                instructions.Add(lastRet);

                for (var index = 0; index < instructions.Count - 1; index++)
                {
                    var instruction = instructions[index];
                    if (instruction.OpCode == OpCodes.Ret)
                    {
                        Instruction leaveInstruction = Instruction.Create(OpCodes.Leave, lastRet);
                        if (instruction == formallyLastInstruction)
                        {
                            lastLeaveInstruction = leaveInstruction;
                        }

                        instructions[index] = leaveInstruction;
                    }
                }

                FixBranchTargets(lastLeaveInstruction, formallyLastInstruction, body);
                return lastRet;
            }
            else
            {
                var instructions = body.Instructions;
                var returnVariable = new VariableDefinition("methodTimerReturn", med.ReturnType);
                body.Variables.Add(returnVariable);
                var lastLd = Instruction.Create(OpCodes.Ldloc, returnVariable);
                instructions.Add(lastLd);
                instructions.Add(Instruction.Create(OpCodes.Ret));

                for (var index = 0; index < instructions.Count - 2; index++)
                {
                    var instruction = instructions[index];
                    if (instruction.OpCode == OpCodes.Ret)
                    {
                        Instruction leaveInstruction = Instruction.Create(OpCodes.Leave, lastLd);
                        if (instruction == formallyLastInstruction)
                        {
                            lastLeaveInstruction = leaveInstruction;
                        }

                        instructions[index] = leaveInstruction;
                        instructions.Insert(index, Instruction.Create(OpCodes.Stloc, returnVariable));
                        index++;
                    }
                }

                FixBranchTargets(lastLeaveInstruction, formallyLastInstruction, body);
                return lastLd;
            }
        }

        /// <nn />
        private static void FixBranchTargets(
          Instruction lastLeaveInstruction,
          Instruction formallyLastRetInstruction,
          MethodBody body)
        {
            for (var index = 0; index < body.Instructions.Count - 2; index++)
            {
                var instruction = body.Instructions[index];
                if (instruction.Operand != null && instruction.Operand == formallyLastRetInstruction)
                {
                    instruction.Operand = lastLeaveInstruction;
                }
            }
        }

        private static Tuple<bool, TestId> IsSequencePointAtStartOfAUnitTest(Mono.Cecil.Cil.SequencePoint sp, FilePath assemblyPath, PerAssemblyTestCases testsPerAssembly)
        {
            if (sp == null)
            {
                return new Tuple<bool, TestId>(false, null);
            }

            if (!testsPerAssembly.ContainsKey(assemblyPath))
            {
                return new Tuple<bool, TestId>(false, null);
            }

            if (testsPerAssembly[assemblyPath].Any(tc => tc.CodeFilePath.ToUpperInvariant() == sp.Document.Url.ToUpperInvariant() && tc.LineNumber == sp.StartLine))
            {
                return new Tuple<bool, TestId>(
                    true,
                    new TestId(
                        assemblyPath,
                        FilePath.NewFilePath(sp.Document.Url),
                        DocumentCoordinate.NewDocumentCoordinate(sp.StartLine)));
            }
            else
            {
                return new Tuple<bool, TestId>(false, null);
            }
        }

        public static CustomAttribute GetAsyncStateMachineAttribute(this MethodDefinition method)
        {
            var asyncAttribute = method.CustomAttributes.FirstOrDefault(_ => _.AttributeType.Name == "AsyncStateMachineAttribute");
            return asyncAttribute;
        }

        public static bool IsAsync(this MethodDefinition method)
        {
            return GetAsyncStateMachineAttribute(method) != null;
        }
    }
}
