// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using BuildXL.Utilities.Tasks;
using BuildXL.Ide.LanguageServer.Utilities;
#if FEATURE_MICROSOFT_DIAGNOSTICS_TRACING
using Microsoft.Diagnostics.Tracing;
#else
using System.Diagnostics.Tracing;
#endif
using StreamJsonRpc;

namespace BuildXL.Ide.JsonRpc
{
    /// <nodoc />
    internal sealed class OutputWindowReporter : IOutputWindowReporter
    {
        private const string TraceTargetName = "dscript/outputTrace";
        private readonly StreamJsonRpc.JsonRpc m_pushRpc;

        public OutputWindowReporter(StreamJsonRpc.JsonRpc pushRpc)
        {
            m_pushRpc = pushRpc;
        }

        /// <inheritdoc />
        public void WriteLine(EventLevel level, string message)
        {
            m_pushRpc
                .NotifyWithParameterObjectAsync(TraceTargetName, LogMessageParams.Create(level, message))
                .IgnoreErrors();
        }
    }
}
