﻿using System;
using System.Collections.Generic;
using System.Linq;
using R4nd0mApps.TddStud10.Common.Domain;

namespace R4nd0mApps.TddStud10.Engine
{
    public class CoverageData
    {
        private static CoverageData _instance;
        public static CoverageData Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new CoverageData();
                }

                return _instance;
            }
        }

        public FilePath SolutionPath { get; set; }

        public PerAssemblySequencePointsCoverage PerAssemblySequencePointsCoverage { get; set; }

        public PerTestIdResults PerTestIdResults { get; set; }

        public PerDocumentSequencePoints PerDocumentSequencePoints { get; set; }

        public void UpdateCoverageResults(RunData rd)
        {
            SolutionPath = rd.startParams.solutionPath;
            PerDocumentSequencePoints = rd.sequencePoints == Microsoft.FSharp.Core.FSharpOption<PerDocumentSequencePoints>.None ? new PerDocumentSequencePoints() : rd.sequencePoints.Value;
            PerAssemblySequencePointsCoverage = rd.codeCoverageResults == Microsoft.FSharp.Core.FSharpOption<PerAssemblySequencePointsCoverage>.None ? new PerAssemblySequencePointsCoverage() : rd.codeCoverageResults.Value;
            PerTestIdResults = rd.executedTests == Microsoft.FSharp.Core.FSharpOption<PerTestIdResults>.None ? new PerTestIdResults() : rd.executedTests.Value;

            var handler = NewCoverageDataAvailable;
            if (NewCoverageDataAvailable != null)
            {
                NewCoverageDataAvailable(this, EventArgs.Empty);
            }
        }

        public event EventHandler NewCoverageDataAvailable;

        public IEnumerable<FilePath> GetAllFiles()
        {
            return from kvp in PerDocumentSequencePoints
                   select kvp.Key;
        }

        public IEnumerable<SequencePoint> GetAllSequencePoints()
        {
            return from kvp in PerDocumentSequencePoints
                   from sps in kvp.Value
                   select sps;
        }

        public IEnumerable<TestRunId> GetUnitTestsCoveringSequencePoint(SequencePoint sequencePoint)
        {
            var unitTests = from kvp in PerAssemblySequencePointsCoverage
                            from chi in kvp.Value
                            where chi.methodId.Equals(sequencePoint.methodId)
                            select chi.testRunId;
            return unitTests.Distinct();
        }
    }
}
