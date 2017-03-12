﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Exceptions;
using dnSpy.Debugger.Impl;

namespace dnSpy.Debugger.Exceptions {
	interface IDbgExceptionSettingsServiceListener {
		void Initialize(DbgExceptionSettingsService dbgExceptionSettingsService);
	}

	[Export(typeof(DbgExceptionSettingsService))]
	sealed class DbgExceptionSettingsServiceImpl : DbgExceptionSettingsService {
		public override event EventHandler<DbgCollectionChangedEventArgs<DbgExceptionSettingsInfo>> ExceptionsChanged;
		public override DbgExceptionSettingsInfo[] Exceptions {
			get {
				lock (lockObj)
					return toExceptionInfo.Select(a => new DbgExceptionSettingsInfo(a.Value.Definition, a.Value.Settings)).ToArray();
			}
		}

		readonly object lockObj;
		readonly DbgDispatcher dbgDispatcher;
		readonly DefaultExceptionDefinitionsProvider defaultExceptionDefinitionsProvider;
		readonly Dictionary<DbgExceptionId, ExceptionInfo> toExceptionInfo;

		sealed class ExceptionInfo {
			public DbgExceptionDefinition Definition { get; }
			public DbgExceptionSettings Settings { get; set; }
			public ExceptionInfo(DbgExceptionDefinition definition, DbgExceptionSettings settings) {
				Definition = definition;
				Settings = settings;
			}
		}

		[ImportingConstructor]
		DbgExceptionSettingsServiceImpl(DbgDispatcher dbgDispatcher, DefaultExceptionDefinitionsProvider defaultExceptionDefinitionsProvider, [ImportMany] IEnumerable<Lazy<IDbgExceptionSettingsServiceListener>> dbgExceptionSettingsServiceListeners) {
			lockObj = new object();
			this.dbgDispatcher = dbgDispatcher;
			this.defaultExceptionDefinitionsProvider = defaultExceptionDefinitionsProvider;
			toExceptionInfo = new Dictionary<DbgExceptionId, ExceptionInfo>();

			foreach (var lz in dbgExceptionSettingsServiceListeners)
				lz.Value.Initialize(this);
		}

		void Dbg(Action action) => dbgDispatcher.Dbg(action);

		public override void Reset() => Dbg(() => ResetCore());

		void ResetCore() {
			dbgDispatcher.VerifyAccess();
			DbgExceptionSettingsInfo[] removed;
			DbgExceptionSettingsInfo[] added;
			lock (lockObj) {
				removed = toExceptionInfo.Values.Select(a => new DbgExceptionSettingsInfo(a.Definition, a.Settings)).ToArray();
				toExceptionInfo.Clear();
				foreach (var def in defaultExceptionDefinitionsProvider.Definitions)
					toExceptionInfo[def.Id] = new ExceptionInfo(def, new DbgExceptionSettings(def.Flags));
				added = toExceptionInfo.Values.Select(a => new DbgExceptionSettingsInfo(a.Definition, a.Settings)).ToArray();
			}
			if (removed.Length > 0)
				ExceptionsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgExceptionSettingsInfo>(removed, added: false));
			if (added.Length > 0)
				ExceptionsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgExceptionSettingsInfo>(added, added: true));
		}

		public override event EventHandler<DbgExceptionSettingsModifiedEventArgs> ExceptionSettingsModified;
		public override void Modify(DbgExceptionIdAndSettings[] settings) {
			if (settings == null)
				throw new ArgumentNullException(nameof(settings));
			Dbg(() => ModifyCore(settings));
		}

		void ModifyCore(DbgExceptionIdAndSettings[] settings) {
			dbgDispatcher.VerifyAccess();
			var modified = new List<DbgExceptionIdAndSettings>(settings.Length);
			lock (lockObj) {
				foreach (var s in settings) {
					if (!toExceptionInfo.TryGetValue(s.Id, out var info))
						continue;
					Debug.Assert(s.Settings.Conditions != null);
					if (s.Settings.Conditions == null)
						continue;
					if (info.Settings == s.Settings)
						continue;
					info.Settings = s.Settings;
					modified.Add(s);
				}
			}
			if (modified.Count > 0)
				ExceptionSettingsModified?.Invoke(this, new DbgExceptionSettingsModifiedEventArgs(modified.ToArray()));
		}

		public override void Remove(DbgExceptionId[] ids) {
			if (ids == null)
				throw new ArgumentNullException(nameof(ids));
			Dbg(() => RemoveCore(ids));
		}

		void RemoveCore(DbgExceptionId[] ids) {
			dbgDispatcher.VerifyAccess();
			var removed = new List<DbgExceptionSettingsInfo>(ids.Length);
			lock (lockObj) {
				foreach (var id in ids) {
					if (!toExceptionInfo.TryGetValue(id, out var info))
						continue;
					toExceptionInfo.Remove(id);
					removed.Add(new DbgExceptionSettingsInfo(info.Definition, info.Settings));
				}
			}
			if (removed.Count > 0)
				ExceptionsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgExceptionSettingsInfo>(removed.ToArray(), added: false));
		}

		public override void Add(DbgExceptionSettingsInfo[] settings) {
			if (settings == null)
				throw new ArgumentNullException(nameof(settings));
			Dbg(() => AddCore(settings));
		}

		void AddCore(DbgExceptionSettingsInfo[] settings) {
			dbgDispatcher.VerifyAccess();
			var added = new List<DbgExceptionSettingsInfo>(settings.Length);
			lock (lockObj) {
				foreach (var s in settings) {
					if (toExceptionInfo.ContainsKey(s.Definition.Id))
						continue;
					bool b = s.Definition.Id.Group != null && s.Settings.Conditions != null;
					Debug.Assert(b);
					if (!b)
						continue;
					var info = new ExceptionInfo(s.Definition, s.Settings);
					toExceptionInfo.Add(s.Definition.Id, info);
					added.Add(new DbgExceptionSettingsInfo(info.Definition, info.Settings));
				}
			}
			if (added.Count > 0)
				ExceptionsChanged?.Invoke(this, new DbgCollectionChangedEventArgs<DbgExceptionSettingsInfo>(added.ToArray(), added: true));
		}

		public override bool TryGetSettings(DbgExceptionId id, out DbgExceptionSettings settings) {
			if (id.Group == null)
				throw new ArgumentException();
			lock (lockObj) {
				if (toExceptionInfo.TryGetValue(id, out var info)) {
					settings = info.Settings;
					return true;
				}
			}
			settings = default(DbgExceptionSettings);
			return false;
		}

		public override DbgExceptionSettings GetSettings(DbgExceptionId id) {
			if (id.Group == null)
				throw new ArgumentException();
			lock (lockObj) {
				if (toExceptionInfo.TryGetValue(id, out var info))
					return info.Settings;
				if (toExceptionInfo.TryGetValue(new DbgExceptionId(id.Group), out info))
					return info.Settings;
			}
			return new DbgExceptionSettings(DbgExceptionDefinitionFlags.None);
		}

		public override DbgExceptionGroupDefinition[] GroupDefinitions => defaultExceptionDefinitionsProvider.GroupDefinitions;

		public override bool TryGetGroupDefinition(string groupName, out DbgExceptionGroupDefinition definition) {
			if (groupName == null)
				throw new ArgumentNullException(nameof(groupName));
			foreach (var group in GroupDefinitions) {
				if (group.Name == groupName) {
					definition = group;
					return true;
				}
			}
			definition = default(DbgExceptionGroupDefinition);
			return false;
		}
	}
}