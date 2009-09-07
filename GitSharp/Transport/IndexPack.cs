﻿/*
 * Copyright (C) 2008, Robin Rosenberg <robin.rosenberg@dewire.com>
 * Copyright (C) 2008, Shawn O. Pearce <spearce@spearce.org>
 *
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or
 * without modification, are permitted provided that the following
 * conditions are met:
 *
 * - Redistributions of source code must retain the above copyright
 *   notice, this list of conditions and the following disclaimer.
 *
 * - Redistributions in binary form must reproduce the above
 *   copyright notice, this list of conditions and the following
 *   disclaimer in the documentation and/or other materials provided
 *   with the distribution.
 *
 * - Neither the name of the Git Development Community nor the
 *   names of its contributors may be used to endorse or promote
 *   products derived from this software without specific prior
 *   written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
 * OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
 * NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
 * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
 * STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF
 * ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitSharp.Exceptions;
using GitSharp.Util;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace GitSharp.Transport
{
	public class IndexPack
	{
		public const string PROGRESS_DOWNLOAD = "Receiving objects";
		public const string PROGRESS_RESOLVE_DELTA = "Resolving deltas";
		public const string PackSuffix = ".pack";
		public const string IndexSuffix = ".idx";

		public const int BUFFER_SIZE = 8192;

		private readonly Repository _repo;
		private readonly FileStream _packOut;
		private readonly Stream _stream;
		private readonly byte[] _buffer;
		private readonly MessageDigest _objectDigest;
		private readonly MutableObjectId _tempObjectId;
		private readonly Crc32 _crc;

		private Inflater _inflater;
		private long _bBase;
		private int _bOffset;
		private int _bAvail;
		private ObjectChecker _objCheck;
		private bool _fixThin;
		private bool _keepEmpty;
		private int _outputVersion;
		private readonly FileInfo _dstPack;
		private readonly FileInfo _dstIdx;
		private long _objectCount;
		private PackedObjectInfo[] _entries;
		private int _deltaCount;
		private int _entryCount;
		private ObjectIdSubclassMap<DeltaChain> _baseById;
		private Dictionary<long, UnresolvedDelta> _baseByPos;
		private byte[] _objectData;
		private MessageDigest _packDigest;
		private byte[] _packcsum;
		private long _originalEof;
		private WindowCursor _windowCursor;

		public IndexPack(Repository db, Stream src, FileInfo dstBase)
		{
			_repo = db;
			_stream = src;
			_crc = new Crc32();
			_inflater = InflaterCache.Instance.get();
			_windowCursor = new WindowCursor();
			_buffer = new byte[BUFFER_SIZE];
			_objectData = new byte[BUFFER_SIZE];
			_objectDigest = Constants.newMessageDigest();
			_tempObjectId = new MutableObjectId();
			_packDigest = Constants.newMessageDigest();

			if (dstBase != null)
			{
				DirectoryInfo dir = dstBase.Directory;
				string nam = dstBase.Name;
				_dstPack = new FileInfo(Path.Combine(dir.ToString(), GetPackFileName(nam)));
				_dstIdx = new FileInfo(Path.Combine(dir.ToString(), GetIndexFileName(nam)));
				_packOut = _dstPack.Create();
			}
			else
			{
				_dstPack = null;
				_dstIdx = null;
			}
		}

		public void setIndexVersion(int version)
		{
			_outputVersion = version;
		}

		public void setFixThin(bool fix)
		{
			_fixThin = fix;
		}

		public void setKeepEmpty(bool empty)
		{
			_keepEmpty = empty;
		}

		public void setObjectChecker(ObjectChecker oc)
		{
			_objCheck = oc;
		}

		public void setObjectChecking(bool on)
		{
			setObjectChecker(on ? new ObjectChecker() : null);
		}

		public void index(IProgressMonitor progress)
		{
			progress.Start(2);
			try
			{
				try
				{
					ReadPackHeader();

					_entries = new PackedObjectInfo[(int)_objectCount];
					_baseById = new ObjectIdSubclassMap<DeltaChain>();
					_baseByPos = new Dictionary<long, UnresolvedDelta>();

					progress.BeginTask(PROGRESS_DOWNLOAD, (int)_objectCount);
					for (int done = 0; done < _objectCount; done++)
					{
						IndexOneObject();
						progress.Update(1);
						if (progress.IsCancelled)
						{
							throw new IOException("Download cancelled");
						}
					}

					ReadPackFooter();
					EndInput();
					progress.EndTask();

					if (_deltaCount > 0)
					{
						if (_packOut == null)
						{
							throw new IOException("need packOut");
						}

						ResolveDeltas(progress);
						if (_entryCount < _objectCount)
						{
							if (!_fixThin)
							{
								throw new IOException("pack has " + (_objectCount - _entryCount) + " unresolved deltas");
							}

							FixThinPack(progress);
						}
					}

					if (_packOut != null && (_keepEmpty || _entryCount > 0))
					{
						_packOut.Flush();
					}

					_packDigest = null;
					_baseById = null;
					_baseByPos = null;

					if (_dstIdx != null && (_keepEmpty || _entryCount > 0))
					{
						WriteIdx();
					}
				}
				finally
				{
					try
					{
						InflaterCache.Instance.release(_inflater);
					}
					finally
					{
						_inflater = null;
					}
					_windowCursor = WindowCursor.Release(_windowCursor);

					progress.EndTask();
					if (_packOut != null)
					{
						_packOut.Close();
					}
				}

				if (_keepEmpty || _entryCount > 0)
				{
					if (_dstPack != null)
					{
						_dstPack.IsReadOnly = true;
					}
					if (_dstIdx != null)
					{
						_dstIdx.IsReadOnly = true;
					}
				}
			}
			catch (IOException)
			{
				if (_dstPack != null) _dstPack.Delete();
				if (_dstIdx != null) _dstIdx.Delete();
				throw;
			}
		}

		private void ResolveDeltas(IProgressMonitor progress)
		{
			progress.BeginTask(PROGRESS_RESOLVE_DELTA, _deltaCount);
			int last = _entryCount;
			for (int i = 0; i < last; i++)
			{
				int before = _entryCount;
				ResolveDeltas(_entries[i]);
				progress.Update(_entryCount - before);
				if (progress.IsCancelled)
				{
					throw new IOException("Download cancelled during indexing");
				}
			}
			progress.EndTask();
		}

		private void ResolveDeltas(PackedObjectInfo objectInfo)
		{
			int oldCrc = objectInfo.CRC;
			if (_baseById.get(objectInfo) != null || _baseByPos.ContainsKey(objectInfo.Offset))
			{
				ResolveDeltas(objectInfo.Offset, oldCrc, Constants.OBJ_BAD, null, objectInfo);
			}
		}

		private void ResolveDeltas(long pos, int oldCrc, int type, byte[] data, PackedObjectInfo oe)
		{
			_crc.Reset();
			Position(pos);
			int c = ReadFromFile();
			int typecode = (c >> 4) & 7;
			long sz = c & 15;
			int shift = 4;
			while ((c & 0x80) != 0)
			{
				c = ReadFromFile();
				sz += (c & 0x7f) << shift;
				shift += 7;
			}

			switch (typecode)
			{
				case Constants.OBJ_COMMIT:
				case Constants.OBJ_TREE:
				case Constants.OBJ_BLOB:
				case Constants.OBJ_TAG:
					type = typecode;
					data = InflateFromFile((int)sz);
					break;

				case Constants.OBJ_OFS_DELTA:
					c = ReadFromFile() & 0xff;
					while ((c & 128) != 0)
					{
						c = ReadFromFile() & 0xff;
					}
					data = BinaryDelta.Apply(data, InflateFromFile((int)sz));
					break;

				case Constants.OBJ_REF_DELTA:
					_crc.Update(_buffer, FillFromFile(20), 20);
					Use(20);
					data = BinaryDelta.Apply(data, InflateFromFile((int)sz));
					break;

				default:
					throw new IOException("Unknown object type " + typecode + ".");
			}

			var crc32 = (int)_crc.Value;
			if (oldCrc != crc32)
			{
				throw new IOException("Corruption detected re-reading at " + pos);
			}

			if (oe == null)
			{
				_objectDigest.Update(Constants.encodedTypeString(type));
				_objectDigest.Update((byte)' ');
				_objectDigest.Update(Constants.encodeASCII(data.Length));
				_objectDigest.Update(0);
				_objectDigest.Update(data);
				_tempObjectId.FromRaw(_objectDigest.Digest(), 0);

				VerifySafeObject(_tempObjectId, type, data);
				oe = new PackedObjectInfo(pos, crc32, _tempObjectId);
				_entries[_entryCount++] = oe;
			}

			ResolveChildDeltas(pos, type, data, oe);
		}

		private UnresolvedDelta RemoveBaseById(AnyObjectId id)
		{
			DeltaChain d = _baseById.get(id);
			return d != null ? d.Remove() : null;
		}

		private void ResolveChildDeltas(long pos, int type, byte[] data, AnyObjectId objectId)
		{
			UnresolvedDelta a = Reverse(RemoveBaseById(objectId));
			UnresolvedDelta b;

			if (_baseByPos.ContainsKey(pos))
			{
				b = Reverse(_baseByPos[pos]);
				_baseByPos.Remove(pos);
			}
			else
			{
				b = Reverse(null);
			}

			while (a != null && b != null)
			{
				if (a.HeaderOffset < b.HeaderOffset)
				{
					ResolveDeltas(a.HeaderOffset, a.Crc32, type, data, null);
					a = a.Next;
				}
				else
				{
					ResolveDeltas(b.HeaderOffset, b.Crc32, type, data, null);
					b = b.Next;
				}
			}

			ResolveChildDeltaChain(type, data, a);
			ResolveChildDeltaChain(type, data, b);
		}

		private void ResolveChildDeltaChain(int type, byte[] data, UnresolvedDelta a)
		{
			while (a != null)
			{
				ResolveDeltas(a.HeaderOffset, a.Crc32, type, data, null);
				a = a.Next;
			}
		}

		private void FixThinPack(IProgressMonitor progress)
		{
			GrowEntries();

			_packDigest.Reset();
			_originalEof = _packOut.Length - 20;
			var def = new Deflater(Deflater.DEFAULT_COMPRESSION, false);
			var missing = new List<DeltaChain>(64);
			long end = _originalEof;

			foreach (DeltaChain baseId in _baseById)
			{
				if (baseId.Head == null)
				{
					missing.Add(baseId);
					continue;
				}

				ObjectLoader ldr = _repo.OpenObject(_windowCursor, baseId);
				if (ldr == null)
				{
					missing.Add(baseId);
					continue;
				}

				byte[] data = ldr.CachedBytes;
				int typeCode = ldr.Type;

				_crc.Reset();
				_packOut.Seek(end, SeekOrigin.Begin);
				WriteWhole(def, typeCode, data);
				var oe = new PackedObjectInfo(end, (int)_crc.Value, baseId);
				_entries[_entryCount++] = oe;
				end = _packOut.Position;

				ResolveChildDeltas(oe.Offset, typeCode, data, oe);
				if (progress.IsCancelled)
				{
					throw new IOException("Download cancelled during indexing");
				}
			}

			def.Finish();

			foreach (DeltaChain baseDeltaChain in missing)
			{
				if (baseDeltaChain.Head != null)
				{
					throw new MissingObjectException(baseDeltaChain, "delta base");
				}
			}

			FixHeaderFooter(_packcsum, _packDigest.Digest());
		}

		private void WriteWhole(Deflater def, int typeCode, byte[] data)
		{
			int sz = data.Length;
			int hdrlen = 0;
			_buffer[hdrlen++] = (byte)((typeCode << 4) | sz & 15);
			sz = (int)(((uint)sz) >> 7);
			while (sz > 0)
			{
				_buffer[hdrlen - 1] |= 0x80;
				_buffer[hdrlen++] = (byte)(sz & 0x7f);
				sz = (int)(((uint)sz) >> 7);
			}
			_packDigest.Update(_buffer, 0, hdrlen);
			_crc.Update(_buffer, 0, hdrlen);
			_packOut.Write(_buffer, 0, hdrlen);
			def.Reset();
			def.SetInput(data);
			def.Finish();
			while (!def.IsFinished)
			{
				int datlen = def.Deflate(_buffer);
				_packDigest.Update(_buffer, 0, datlen);
				_crc.Update(_buffer, 0, datlen);
				_packOut.Write(_buffer, 0, datlen);
			}
		}

		private void FixHeaderFooter(IEnumerable<byte> origcsum, IEnumerable<byte> tailcsum)
		{
			MessageDigest origDigest = Constants.newMessageDigest();
			MessageDigest tailDigest = Constants.newMessageDigest();
			long origRemaining = _originalEof;

			_packOut.Seek(0, SeekOrigin.Begin);
			_bAvail = 0;
			_bOffset = 0;
			FillFromFile(12);

			{
				var origCnt = (int)Math.Min(_bAvail, origRemaining);
				origDigest.Update(_buffer, 0, origCnt);
				origRemaining -= origCnt;
				if (origRemaining == 0)
				{
					tailDigest.Update(_buffer, origCnt, _bAvail - origCnt);
				}
			}

			NB.encodeInt32(_buffer, 8, _entryCount);
			_packOut.Seek(0, SeekOrigin.Begin);
			_packOut.Write(_buffer, 0, 12);
			_packOut.Seek(_bAvail, SeekOrigin.Begin);

			_packDigest.Reset();
			_packDigest.Update(_buffer, 0, _bAvail);

			while (true)
			{
				int n = _packOut.Read(_buffer, 0, _buffer.Length);
				if (n <= 0) break;

				if (origRemaining != 0)
				{
					var origCnt = (int)Math.Min(n, origRemaining);
					origDigest.Update(_buffer, 0, origCnt);
					origRemaining -= origCnt;
					if (origRemaining == 0)
					{
						tailDigest.Update(_buffer, origCnt, n - origCnt);
					}
				}
				else
				{
					tailDigest.Update(_buffer, 0, n);
				}

				_packDigest.Update(_buffer, 0, n);
			}

			if (!origDigest.Digest().SequenceEqual(origcsum) || !tailDigest.Digest().SequenceEqual(tailcsum))
			{
				throw new IOException("Pack corrupted while writing to filesystem");
			}

			_packcsum = _packDigest.Digest();
			_packOut.Write(_packcsum, 0, _packcsum.Length);
		}

		private void GrowEntries()
		{
			var newEntries = new PackedObjectInfo[(int)_objectCount + _baseById.size()];
			Array.Copy(_entries, 0, newEntries, 0, _entryCount);
			_entries = newEntries;
		}

		private void WriteIdx()
		{
			Array.Sort(_entries, 0, _entryCount);
			var list = new List<PackedObjectInfo>(_entries);
			if (_entryCount < _entries.Length)
			{
				list.RemoveRange(_entryCount, _entries.Length - _entryCount);
			}

			FileStream os = _dstIdx.Create();
			try
			{
				PackIndexWriter iw = _outputVersion <= 0 ?
					PackIndexWriter.CreateOldestPossible(os, list) :
					PackIndexWriter.CreateVersion(os, _outputVersion);

				iw.Write(list, _packcsum);
				os.Flush();
			}
			finally
			{
				os.Close();
			}
		}

		private void ReadPackHeader()
		{
			int hdrln = Constants.PACK_SIGNATURE.Length + 4 + 4;
			int p = FillFromInput(hdrln);
			for (int k = 0; k < Constants.PACK_SIGNATURE.Length; k++)
			{
				if (_buffer[p + k] != Constants.PACK_SIGNATURE[k])
				{
					throw new IOException("Not a PACK file.");
				}
			}

			long vers = NB.decodeInt32(_buffer, p + 4);
			if (vers != 2 && vers != 3)
			{
				throw new IOException("Unsupported pack version " + vers + ".");
			}

			_objectCount = NB.decodeUInt32(_buffer, p + 8);
			Use(hdrln);
		}

		private void ReadPackFooter()
		{
			Sync();
			byte[] cmpcsum = _packDigest.Digest();
			int c = FillFromInput(20);
			_packcsum = new byte[20];
			Array.Copy(_buffer, c, _packcsum, 0, 20);

			Use(20);

			if (_packOut != null)
			{
				_packOut.Write(_packcsum, 0, _packcsum.Length);
			}

			if (!cmpcsum.ArrayEquals(_packcsum))
			{
				throw new CorruptObjectException("Packfile checksum incorrect.");
			}
		}

		private void EndInput()
		{
			_objectData = null;
		}

		private void IndexOneObject()
		{
			long pos = Position();
			_crc.Reset();
			int c = ReadFromInput();
			int typeCode = (c >> 4) & 7;
			long sz = c & 15;
			int shift = 4;
			while ((c & 0x80) != 0)
			{
				c = ReadFromInput();
				sz += (c & 0x7f) << shift;
				shift += 7;
			}

			switch (typeCode)
			{
				case Constants.OBJ_COMMIT:
				case Constants.OBJ_TREE:
				case Constants.OBJ_BLOB:
				case Constants.OBJ_TAG:
					Whole(typeCode, pos, sz);
					break;
				case Constants.OBJ_OFS_DELTA:
					c = ReadFromInput();
					long ofs = c & 127;
					while ((c & 128) != 0)
					{
						ofs += 1;
						c = ReadFromInput();
						ofs <<= 7;
						ofs += (c & 127);
					}
					long pbase = pos - ofs;
					SkipInflateFromInput(sz);
					var n = new UnresolvedDelta(pos, (int)_crc.Value);
					if (_baseByPos.ContainsKey(pbase))
					{
						n.Next = _baseByPos[pbase];
						_baseByPos[pbase] = n;
					}
					else
					{
						_baseByPos.Add(pbase, n);
					}
					_deltaCount++;
					break;

				case Constants.OBJ_REF_DELTA:
					c = FillFromInput(20);
					_crc.Update(_buffer, c, 20);
					ObjectId baseId = ObjectId.FromRaw(_buffer, c);
					Use(20);
					DeltaChain r = _baseById.get(baseId);
					if (r == null)
					{
						r = new DeltaChain(baseId);
						_baseById.add(r);
					}
					SkipInflateFromInput(sz);
					r.Add(new UnresolvedDelta(pos, (int)_crc.Value));
					_deltaCount++;
					break;

				default:
					throw new IOException("Unknown object type " + typeCode + ".");
			}
		}

		private void Whole(int type, long pos, long sz)
		{
			byte[] data = InflateFromInput((int)sz);
			_objectDigest.Update(Constants.encodedTypeString(type));
			_objectDigest.Update((byte)' ');
			_objectDigest.Update(Constants.encodeASCII(sz));
			_objectDigest.Update(0);
			_objectDigest.Update(data);
			_tempObjectId.FromRaw(_objectDigest.Digest(), 0);

			VerifySafeObject(_tempObjectId, type, data);
			var crc32 = (int)_crc.Value;
			_entries[_entryCount++] = new PackedObjectInfo(pos, crc32, _tempObjectId);
		}

		private void VerifySafeObject(AnyObjectId id, int type, byte[] data)
		{
			if (_objCheck != null)
			{
				try
				{
					_objCheck.check(type, data);
				}
				catch (CorruptObjectException e)
				{
					throw new IOException("Invalid " + Constants.typeString(type) + " " + id + ": " + e.Message, e);
				}
			}

			ObjectLoader ldr = _repo.OpenObject(_windowCursor, id);
			if (ldr != null)
			{
				byte[] existingData = ldr.CachedBytes;
				if (ldr.Type != type || !data.ArrayEquals(existingData))
				{
					throw new IOException("Collision on " + id);
				}
			}
		}

		private long Position()
		{
			return _bBase + _bOffset;
		}

		private void Position(long pos)
		{
			_packOut.Seek(pos, SeekOrigin.Begin);
			_bBase = pos;
			_bOffset = 0;
			_bAvail = 0;
		}

		private int ReadFromInput()
		{
			if (_bAvail == 0)
			{
				FillFromInput(1);
			}

			_bAvail--;
			int b = _buffer[_bOffset++] & 0xff;
			_crc.Update((uint)b);
			return b;
		}

		private int ReadFromFile()
		{
			if (_bAvail == 0)
			{
				FillFromFile(1);
			}

			_bAvail--;
			int b = _buffer[_bOffset++] & 0xff;
			_crc.Update((uint)b);
			return b;
		}

		private void Use(int cnt)
		{
			_bOffset += cnt;
			_bAvail -= cnt;
		}

		private int FillFromInput(int need)
		{
			while (_bAvail < need)
			{
				int next = _bOffset + _bAvail;
				int free = _buffer.Length - next;
				if (free + _bAvail < need)
				{
					Sync();
					next = _bAvail;
					free = _buffer.Length - next;
				}

				int prevNext = next;
				next = _stream.Read(_buffer, next, free);
				if (next <= 0 && (prevNext != _buffer.Length))
				{
					throw new EndOfStreamException("Packfile is truncated,");
				}

				_bAvail += next;
			}
			return _bOffset;
		}

		private int FillFromFile(int need)
		{
			if (_bAvail < need)
			{
				int next = _bOffset + _bAvail;
				int free = _buffer.Length - next;
				if (free + _bAvail < need)
				{
					if (_bAvail > 0)
					{
						Array.Copy(_buffer, _bOffset, _buffer, 0, _bAvail);
					}

					_bOffset = 0;
					next = _bAvail;
					free = _buffer.Length - next;
				}
				int prevNext = next;
				next = _packOut.Read(_buffer, next, free);

				if (next <= 0 && (prevNext != _buffer.Length))
				{
					throw new EndOfStreamException("Packfile is truncated.");
				}

				_bAvail += next;
			}

			return _bOffset;
		}

		private void Sync()
		{
			_packDigest.Update(_buffer, 0, _bOffset);
			if (_packOut != null)
			{
				_packOut.Write(_buffer, 0, _bOffset);
			}

			if (_bAvail > 0)
			{
				Array.Copy(_buffer, _bOffset, _buffer, 0, _bAvail);
			}

			_bBase += _bOffset;
			_bOffset = 0;
		}

		private void SkipInflateFromInput(long sz)
		{
			Inflater inf = _inflater;
			try
			{
				byte[] dst = _objectData;
				int n = 0;
				int p = -1;
				while (!inf.IsFinished)
				{
					if (inf.IsNeedingInput)
					{
						if (p >= 0)
						{
							_crc.Update(_buffer, p, _bAvail);
							Use(_bAvail);
						}
						p = FillFromInput(1);
						inf.SetInput(_buffer, p, _bAvail);
					}

					int free = dst.Length - n;
					if (free < 8)
					{
						sz -= n;
						n = 0;
						free = dst.Length;
					}
					n += inf.Inflate(dst, n, free);
				}

				if (n != sz)
				{
					throw new IOException("wrong decompressed length");
				}

				n = _bAvail - inf.RemainingInput;
				if (n > 0)
				{
					_crc.Update(_buffer, p, n);
					Use(n);
				}
			}
			catch (IOException e)
			{
				throw Corrupt(e);
			}
			finally
			{
				inf.Reset();
			}
		}

		private byte[] InflateFromInput(int size)
		{
			var dst = new byte[size];
			Inflater inf = _inflater;
			try
			{
				int n = 0;
				int p = -1;
				while (!inf.IsFinished)
				{
					if (inf.IsNeedingInput)
					{
						if (p >= 0)
						{
							_crc.Update(_buffer, p, _bAvail);
							Use(_bAvail);
						}
						p = FillFromFile(1);
						inf.SetInput(_buffer, p, _bAvail);
					}

					n += inf.Inflate(dst, n, size - n);
				}
				n = _bAvail - inf.RemainingInput;
				if (n > 0)
				{
					_crc.Update(_buffer, p, n);
					Use(n);
				}
				return dst;
			}
			catch (IOException e)
			{
				throw Corrupt(e);
			}
			finally
			{
				inf.Reset();
			}
		}

		private byte[] InflateFromFile(long size)
		{
			var dst = new byte[(int)size];
			Inflater inf = _inflater;
			try
			{
				int n = 0;
				int p = -1;
				while (!inf.IsFinished)
				{
					if (inf.IsNeedingInput)
					{
						if (p >= 0)
						{
							_crc.Update(_buffer, p, _bAvail);
							Use(_bAvail);
						}
						p = FillFromInput(1);
						inf.SetInput(_buffer, p, _bAvail);
					}

					n += inf.Inflate(dst, n, dst.Length - n);
				}
				if (n != size)
					throw new IOException("wrong decompressed length");
				n = _bAvail - inf.RemainingInput;
				if (n > 0)
				{
					_crc.Update(_buffer, p, n);
					Use(n);
				}
				return dst;
			}
			catch (IOException e)
			{
				throw Corrupt(e);
			}
			finally
			{
				inf.Reset();
			}
		}

		public void renameAndOpenPack()
		{
			renameAndOpenPack(null);
		}

		public PackLock renameAndOpenPack(string lockMessage)
		{
			if (!_keepEmpty && _entryCount == 0)
			{
				CleanupTemporaryFiles();
				return null;
			}

			MessageDigest d = Constants.newMessageDigest();
			var oeBytes = new byte[Constants.OBJECT_ID_LENGTH];
			for (int i = 0; i < _entryCount; i++)
			{
				PackedObjectInfo oe = _entries[i];
				oe.copyRawTo(oeBytes, 0);
				d.Update(oeBytes);
			}

			string name = ObjectId.FromRaw(d.Digest()).Name;
			var packDir = new DirectoryInfo(Path.Combine(_repo.ObjectsDirectory.ToString(), "pack"));
			var finalPack = new FileInfo(Path.Combine(packDir.ToString(), "pack-" + GetPackFileName(name)));
			var finalIdx = new FileInfo(Path.Combine(packDir.ToString(), "pack-" + GetIndexFileName(name)));
			var keep = new PackLock(finalPack);

			if (!packDir.Exists)
			{
				packDir.Create();
				if (!packDir.Exists)
				{
					CleanupTemporaryFiles();
					throw new IOException("Cannot Create " + packDir);
				}
			}

			if (finalPack.Exists)
			{
				CleanupTemporaryFiles();
				return null;
			}

			if (lockMessage != null)
			{
				try
				{
					if (!keep.Lock(lockMessage))
					{
						throw new IOException("Cannot lock pack in " + finalPack);
					}
				}
				catch (IOException)
				{
					CleanupTemporaryFiles();
					throw;
				}
			}

			if (!_dstPack.RenameTo(finalPack.ToString()))
			{
				CleanupTemporaryFiles();
				keep.Unlock();
				throw new IOException("Cannot move pack to " + finalPack);
			}

			if (!_dstIdx.RenameTo(finalIdx.ToString()))
			{
				CleanupTemporaryFiles();
				keep.Unlock();
				finalPack.Delete();
				//if (finalPack.Exists)
				// [caytchen] TODO: finalPack.deleteOnExit();
				throw new IOException("Cannot move index to " + finalIdx);
			}

			try
			{
				_repo.OpenPack(finalPack, finalIdx);
			}
			catch (IOException)
			{
				keep.Unlock();
				finalPack.Delete();
				finalIdx.Delete();
				throw;
			}

			return lockMessage != null ? keep : null;
		}

		private void CleanupTemporaryFiles()
		{
			_dstIdx.Delete();
			//if (_dstIdx.Exists)
			// [caytchen] TODO: _dstIdx.deleteOnExit();
			_dstPack.Delete();
			//if (_dstPack.Exists)
			// [caytchen] TODO: _dstPack.deleteOnExit();
		}

		private static FileInfo CreateTempFile(string pre, string suf, DirectoryInfo dir)
		{
			var r = new Random();
			int randsuf = r.Next(100000, 999999);
			string p = Path.Combine(dir.ToString(), pre + randsuf + suf);
			File.Create(p).Close();
			return new FileInfo(p);
		}

		private static CorruptObjectException Corrupt(IOException e)
		{
			return new CorruptObjectException("Packfile corruption detected: " + e.Message);
		}

		private static UnresolvedDelta Reverse(UnresolvedDelta c)
		{
			UnresolvedDelta tail = null;
			while (c != null)
			{
				UnresolvedDelta n = c.Next;
				c.Next = tail;
				tail = c;
				c = n;
			}
			return tail;
		}

		internal static IndexPack Create(Repository db, Stream stream)
		{
			DirectoryInfo objdir = db.ObjectsDirectory;
			FileInfo tmp = CreateTempFile("incoming_", PackSuffix, objdir);
			string n = tmp.Name;

			var basef = new FileInfo(Path.Combine(objdir.ToString(), n.Slice(0, n.Length - PackSuffix.Length)));
			var ip = new IndexPack(db, stream, basef);
			ip.setIndexVersion(db.Config.getCore().getPackIndexVersion());
			return ip;
		}

		internal static string GetPackFileName(string fileName)
		{
			if (string.IsNullOrEmpty(fileName))
			{
				throw new ArgumentNullException("fileName");
			}
			return fileName + PackSuffix;
		}

		internal static string GetIndexFileName(string fileName)
		{
			if (string.IsNullOrEmpty(fileName))
			{
				throw new ArgumentNullException("fileName");
			}
			return fileName + IndexSuffix;
		}

		#region Nested Types

		private class DeltaChain : ObjectId
		{
			public UnresolvedDelta Head { get; private set; }

			public DeltaChain(AnyObjectId id)
				: base(id)
			{

			}

			public UnresolvedDelta Remove()
			{
				UnresolvedDelta r = Head;
				if (r != null)
				{
					Head = null;
				}

				return r;
			}

			public void Add(UnresolvedDelta d)
			{
				d.Next = Head;
				Head = d;
			}
		}

		private class UnresolvedDelta
		{
			private readonly long _headerOffset;
			private readonly int _crc32;

			public UnresolvedDelta(long headerOffset, int crc32)
			{
				_headerOffset = headerOffset;
				_crc32 = crc32;
			}

			public long HeaderOffset
			{
				get { return _headerOffset; }
			}

			public int Crc32
			{
				get { return _crc32; }
			}

			public UnresolvedDelta Next { get; set; }
		}

		#endregion
	}
}