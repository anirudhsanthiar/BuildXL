﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.IO;
using System.Linq;
using BuildXL.Pips.Operations;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Tasks;
using JetBrains.Annotations;

namespace BuildXL.Processes
{
    /// <summary>
    /// A disposable reader for sideband files.
    /// 
    /// Uses <see cref="FileEnvelope"/> to check the integrity of the given file.
    /// </summary>
    public sealed class SharedOpaqueSidebandFileReader : IDisposable
    {
        private readonly BuildXLReader m_bxlReader;

        /// <nodoc />
        public SharedOpaqueSidebandFileReader(string sidebandFile)
        {
            Contract.Requires(File.Exists(sidebandFile));

            m_bxlReader = new BuildXLReader(
                stream: new FileStream(sidebandFile, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete),
                debug: false,
                leaveOpen: false);
        }

        /// <summary>
        /// Reads the header and returns if the sideband file has been compromised.
        /// 
        /// Even when the sideband file is compromised, it is possible to call <see cref="ReadRecordedPaths"/>
        /// which will then try to recover as many recorded paths as possible.
        /// </summary>
        public bool ReadHeader(bool ignoreChecksum)
        {
            var result = SharedOpaqueOutputLogger.FileEnvelope.TryReadHeader(m_bxlReader.BaseStream, ignoreChecksum);
            return result.Succeeded;
        }

        /// <summary>
        /// Returns all paths recorded in this sideband file.
        /// </summary>
        /// <remarks>
        /// Those paths are expected to be absolute paths of files/directories that were written to by the previous build.
        ///
        /// NOTE: this method does not validate the recorded paths in any way.  That means that each returned string may be
        ///   - a path pointing to an absent file
        ///   - a path pointing to a file
        ///   - a path pointing to a directory.
        /// 
        /// NOTE: if the sideband file was produced by an instance of this class (and wasn't corrupted in any way)
        ///   - the strings in the returned enumerable are all legal paths
        ///   - the returned collection does not contain any duplicates
        /// Whether or not this sideband file is corrupted is determined by the result of the <see cref="ReadHeader"/> method.
        /// </remarks>
        public IEnumerable<string> ReadRecordedPaths()
        {
            string nextString = null;
            while ((nextString = ReadStringOrNull()) != null)
            {
                yield return nextString;
            }
        }

        private string ReadStringOrNull()
        {
            try
            {
                return m_bxlReader.ReadString();
            }
            catch (IOException)
            {
                return null;
            }
        }

        /// <nodoc />
        public void Dispose()
        {
            m_bxlReader.Dispose();
        }
    }

    /// <summary>
    /// Responsible for keeping a log of all paths written to by a given process pip.
    /// 
    /// Paths are flushed to an underlying file as soon as they're reported via 
    /// <see cref="RecordFileWrite(PathTable, AbsolutePath)"/>.
    /// 
    /// A number of root directories may be set in which case the log will record only 
    /// those paths that fall under one of those directories.  The typical use case is to
    /// use all shared opaque directory outputs of a process as root directories.
    /// </summary>
    /// <remarks>
    /// NOTE: not thread-safe
    /// 
    /// NOTE: must be serializable in order to be compatible with VM execution; for this 
    ///       reason, this class must not have a field of type <see cref="PathTable"/>.
    /// </remarks>
    public sealed class SharedOpaqueOutputLogger : IDisposable
    {
        /// <summary>
        /// Envelope for serialization
        /// </summary>
        public static readonly FileEnvelope FileEnvelope = new FileEnvelope(name: "SharedOpaqueSidebandState", version: 0);

        private readonly HashSet<AbsolutePath> m_recordedPathsCache;
        private readonly Lazy<BuildXLWriter> m_lazyBxlWriter;
        private readonly Lazy<Unit> m_lazyWriteHeader;
        private readonly FileEnvelopeId m_envelopeId;

        /// <summary>
        /// Absolute path of the sideband file this logger writes to.
        /// </summary>
        public string SidebandLogFile { get; }

        /// <summary>
        /// Only paths under these root directories will be recorded by <see cref="RecordFileWrite(PathTable, AbsolutePath)"/>
        /// 
        /// When <code>null</code>, all paths are recorded.
        /// </summary>
        [CanBeNull]
        public IReadOnlyList<string> RootDirectories { get; }

        /// <summary>
        /// Creates a new output logger for a given process.
        /// 
        /// Shared opaque directory outputs of <paramref name="process"/> are used as root directories and
        /// <see cref="GetSidebandFileForProcess"/> is used as log base name.
        ///
        /// <seealso cref="SharedOpaqueOutputLogger(string, IReadOnlyList{string})"/>
        /// </summary>
        public SharedOpaqueOutputLogger(PipExecutionContext context, Process process, AbsolutePath sidebandRootDirectory)
            : this(
                  GetSidebandFileForProcess(context.PathTable, sidebandRootDirectory, process),
                  process.DirectoryOutputs.Where(d => d.IsSharedOpaque).Select(d => d.Path.ToString(context.PathTable)).ToList())
        {
            Contract.Requires(process != null);
            Contract.Requires(context != null);
            Contract.Requires(sidebandRootDirectory.IsValid);
            Contract.Requires(Directory.Exists(sidebandRootDirectory.ToString(context.PathTable)));
        }

        /// <summary>
        /// Creates a new output logger.
        /// 
        /// The underlying file is created only upon first write.
        /// </summary>
        /// <param name="sidebandLogFile">File to which to save the log.</param>
        /// <param name="rootDirectories">Only paths under one of the root directories are recorded in <see cref="RecordFileWrite(PathTable, AbsolutePath)"/>.</param>
        public SharedOpaqueOutputLogger(string sidebandLogFile, [CanBeNull] IReadOnlyList<string> rootDirectories)
        {
            SidebandLogFile = sidebandLogFile;
            RootDirectories = rootDirectories;
            m_recordedPathsCache = new HashSet<AbsolutePath>();
            m_envelopeId = FileEnvelopeId.Create();

            m_lazyBxlWriter = Lazy.Create(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SidebandLogFile));
                return new BuildXLWriter(
                    stream: new FileStream(SidebandLogFile, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete),
                    debug: false,
                    logStats: false,
                    leaveOpen: false);
            });

            m_lazyWriteHeader = Lazy.Create(() =>
            {
                FileEnvelope.WriteHeader(m_lazyBxlWriter.Value.BaseStream, m_envelopeId);
                return Unit.Void;
            });
        }

        private const string SidebandFilePrefix = "Pip";
        private const string SidebandFileSuffix = ".sideband";

        /// <summary>
        /// Given a root directory (<paramref name="searchRootDirectory"/>), returns the full path to the sideband file corresponding to process <paramref name="process"/>.
        /// </summary>
        public static string GetSidebandFileForProcess(PathTable pathTable, AbsolutePath searchRootDirectory, Process process)
        {
            Contract.Requires(searchRootDirectory.IsValid);

            var semiStableHashX16 = string.Format(CultureInfo.InvariantCulture, "{0:X16}", process.SemiStableHash);
            var subDirName = semiStableHashX16.Substring(0, 3);

            return searchRootDirectory.Combine(pathTable, subDirName).Combine(pathTable, $"{SidebandFilePrefix}{semiStableHashX16}{SidebandFileSuffix}").ToString(pathTable);
        }

        /// <summary>
        /// Finds and returns all sideband files that exist in directory denoted by <paramref name="directory"/>
        /// </summary>
        /// <remarks>
        /// CODESYNC: must be consistent with <see cref="GetSidebandFileForProcess(PathTable, AbsolutePath, Process)"/>
        /// </remarks>
        public static string[] FindAllProcessPipSidebandFiles(string directory)
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, $"{SidebandFilePrefix}*{SidebandFileSuffix}", SearchOption.AllDirectories).ToArray()
                : CollectionUtilities.EmptyArray<string>();
        }

        /// <summary>
        /// Creates a reader for the given sideband file.
        /// </summary>
        public static SharedOpaqueSidebandFileReader CreateSidebandFileReader(string filePath)
        {
            Contract.Requires(File.Exists(filePath));
            return new SharedOpaqueSidebandFileReader(filePath);
        }

        /// <summary>
        /// Returns all paths recorded in the <paramref name="filePath"/> file, even if the
        /// file appears to be corrupted.
        /// 
        /// If file at <paramref name="filePath"/> does not exist, returns an empty iterator.
        /// 
        /// <seealso cref="SharedOpaqueSidebandFileReader.ReadRecordedPaths"/>
        /// </summary>
        public static string[] ReadRecordedPathsFromSidebandFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return CollectionUtilities.EmptyArray<string>();
            }

            using (var reader = CreateSidebandFileReader(filePath))
            {
                reader.ReadHeader(ignoreChecksum: true);
                return reader.ReadRecordedPaths().ToArray();
            }
        }

        /// <summary>
        /// By calling this method a client can ensure that the sideband file will be created even
        /// if 0 paths are recorded for it.  If this method is not explicitly called, the sideband
        /// file will only be created if at least one write is recorded to it.
        /// </summary>
        public void EnsureHeaderWritten()
        {
            Analysis.IgnoreResult(m_lazyWriteHeader.Value);
        }

        /// <summary>
        /// <see cref="RecordFileWrite(PathTable, AbsolutePath)"/>
        /// </summary>
        public bool RecordFileWrite(PathTable pathTable, string absolutePath)
            => RecordFileWrite(pathTable, AbsolutePath.Create(pathTable, absolutePath));

        /// <summary>
        /// Records that the file at location <paramref name="path"/> was written to.
        /// </summary>
        /// <returns>
        /// <code>true</code> if <paramref name="path"/> is within any given root directory
        /// (<see cref="RootDirectories"/>) and was recorded this time around (i.e., wasn't
        /// a duplicate of a previously recorded path); <code>false</code> otherwise.
        /// </returns>
        /// <remarks>
        /// NOT THREAD-SAFE.
        /// </remarks>
        public bool RecordFileWrite(PathTable pathTable, AbsolutePath path)
        {
            if (RootDirectories == null || RootDirectories.Any(dir => path.IsWithin(pathTable, AbsolutePath.Create(pathTable, dir))))
            {
                if (m_recordedPathsCache.Add(path))
                {
                    EnsureHeaderWritten();
                    m_lazyBxlWriter.Value.Write(path.ToString(pathTable));
                    m_lazyBxlWriter.Value.Flush();
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Don't use, for testing only.
        /// </summary>
        internal void CloseWriterWithoutFixingUpHeaderForTestingOnly()
        {
            m_lazyBxlWriter.Value.Close();
        }

        /// <nodoc />
        public void Dispose()
        {
            // NOTE: it is essential not to call m_lazyBxlWriter.Value.Dispose() if bxlWriter hasn't been created.
            // 
            // reason: 
            //   - when running a process in VM, a logger is created twice for that process: (1) first in the
            //     bxl process, and (2) second in the VM process
            //   - the VM process then runs, and writes stuff to its instance of this logger; once it finishes, 
            //     all shared opaque output writes are saved to the underlying sideband file
            //   - the bxl process disposes its instance of this logger; without the check below, the Dispose method
            //     creates a BuildXLWriter for the same underlying sideband file and immediately closes it, which
            //     effectively deletes the content of that file.
            if (m_lazyBxlWriter.IsValueCreated)
            {
                FileEnvelope.FixUpHeader(m_lazyBxlWriter.Value.BaseStream, m_envelopeId);
                m_lazyBxlWriter.Value.Dispose();
            }
        }

        #region Serialization
        /// <nodoc />
        public void Serialize(BuildXLWriter writer)
        {
            writer.Write(SidebandLogFile);
            writer.Write(RootDirectories, (w, list) => w.WriteReadOnlyList(list, (w2, path) => w2.Write(path)));
        }

        /// <nodoc />
        public static SharedOpaqueOutputLogger Deserialize(BuildXLReader reader)
        {
            return new SharedOpaqueOutputLogger(
                sidebandLogFile: reader.ReadString(),
                rootDirectories: reader.ReadNullable(r => r.ReadReadOnlyList(r2 => r2.ReadString())));
        }
        #endregion
    }
}
