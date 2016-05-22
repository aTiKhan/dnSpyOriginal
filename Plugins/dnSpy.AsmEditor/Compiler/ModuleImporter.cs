﻿/*
    Copyright (C) 2014-2016 de4dot@gmail.com

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
using System.Diagnostics;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.PE;
using dnlib.Threading;
using dnSpy.AsmEditor.Properties;
using dnSpy.Contracts.AsmEditor.Compiler;

namespace dnSpy.AsmEditor.Compiler {
	[Serializable]
	sealed class ModuleImporterAbortedException : Exception {
	}

	sealed partial class ModuleImporter {
		const string IM0001 = nameof(IM0001);
		const string IM0002 = nameof(IM0002);
		const string IM0003 = nameof(IM0003);
		const string IM0004 = nameof(IM0004);
		const string IM0005 = nameof(IM0005);
		const string IM0006 = nameof(IM0006);
		const string IM0007 = nameof(IM0007);
		const string IM0008 = nameof(IM0008);
		const string IM0009 = nameof(IM0009);

		public CompilerDiagnostic[] Diagnostics => diagnostics.ToArray();
		public NewImportedType[] NewNonNestedTypes => newNonNestedImportedTypes.ToArray();
		public MergedImportedType[] MergedNonNestedTypes => nonNestedMergedImportedTypes.Where(a => !a.IsEmpty).ToArray();

		readonly ModuleDef targetModule;
		readonly List<CompilerDiagnostic> diagnostics;
		readonly List<NewImportedType> newNonNestedImportedTypes;
		readonly List<MergedImportedType> nonNestedMergedImportedTypes;

		ModuleDef sourceModule;
		readonly Dictionary<TypeDef, ImportedType> oldTypeToNewType;
		readonly Dictionary<ITypeDefOrRef, ImportedType> oldTypeRefToNewType;
		readonly Dictionary<MethodDef, MethodDef> oldMethodToNewMethod;
		readonly Dictionary<FieldDef, FieldDef> oldFieldToNewField;
		readonly Dictionary<PropertyDef, PropertyDef> oldPropertyToNewProperty;
		readonly Dictionary<EventDef, EventDef> oldEventToNewEvent;
		readonly Dictionary<object, object> bodyDict;
		readonly Dictionary<ImportedType, ExtraImportedTypeData> toExtraData;
		readonly Dictionary<MethodDef, MethodDef> editedMethodsToFix;
		readonly HashSet<object> isStub;
		ImportSigComparerOptions importSigComparerOptions;

		struct ExtraImportedTypeData {
			/// <summary>
			/// New type in temporary module created by the compiler
			/// </summary>
			public TypeDef CompiledType { get; }
			public ExtraImportedTypeData(TypeDef compiledType) {
				CompiledType = compiledType;
			}
		}

		const SigComparerOptions SIG_COMPARER_OPTIONS = SigComparerOptions.TypeRefCanReferenceGlobalType | SigComparerOptions.PrivateScopeIsComparable;

		public ModuleImporter(ModuleDef targetModule) {
			this.targetModule = targetModule;
			this.diagnostics = new List<CompilerDiagnostic>();
			this.newNonNestedImportedTypes = new List<NewImportedType>();
			this.nonNestedMergedImportedTypes = new List<MergedImportedType>();
			this.oldTypeToNewType = new Dictionary<TypeDef, ImportedType>();
			this.oldTypeRefToNewType = new Dictionary<ITypeDefOrRef, ImportedType>(TypeEqualityComparer.Instance);
			this.oldMethodToNewMethod = new Dictionary<MethodDef, MethodDef>();
			this.oldFieldToNewField = new Dictionary<FieldDef, FieldDef>();
			this.oldPropertyToNewProperty = new Dictionary<PropertyDef, PropertyDef>();
			this.oldEventToNewEvent = new Dictionary<EventDef, EventDef>();
			this.bodyDict = new Dictionary<object, object>();
			this.toExtraData = new Dictionary<ImportedType, ExtraImportedTypeData>();
			this.editedMethodsToFix = new Dictionary<MethodDef, MethodDef>();
			this.isStub = new HashSet<object>();
		}

		void AddError(string id, string msg) => diagnostics.Add(new CompilerDiagnostic(CompilerDiagnosticSeverity.Error, msg, id, null, null));

		void AddErrorThrow(string id, string msg) {
			AddError(id, msg);
			throw new ModuleImporterAbortedException();
		}

		ModuleDefMD LoadModule(byte[] rawGeneratedModule, DebugFileResult debugFile) {
			var opts = new ModuleCreationOptions();

			switch (debugFile.Format) {
			case DebugFileFormat.None:
				break;

			case DebugFileFormat.Pdb:
				opts.PdbFileOrData = debugFile.RawFile;
				break;

			case DebugFileFormat.PortablePdb:
				Debug.Fail("Portable PDB isn't supported yet");//TODO:
				break;

			case DebugFileFormat.Embedded:
				Debug.Fail("Embedded Portable PDB isn't supported yet");//TODO:
				break;

			default:
				Debug.Fail($"Unknown debug file format: {debugFile.Format}");
				break;
			}

			return ModuleDefMD.Load(rawGeneratedModule, opts);
		}

		/// <summary>
		/// Imports all new types and methods (compiler generated or created by the user). All new types and members
		/// in the global type are added to the target's global type. All duplicates are renamed.
		/// All removed classes and members in the edited method's type or it's declaring type etc are kept. All
		/// new types and members are added to the target module. Nothing needs to be renamed because a member that
		/// exists in both modules is assumed to be the original member stub.
		/// All the instructions in the edited method are imported, and its impl attributes. Nothing else is imported.
		/// </summary>
		/// <param name="rawGeneratedModule">Raw bytes of compiled assembly</param>
		/// <param name="targetMethod">Original method that was edited</param>
		public void Import(byte[] rawGeneratedModule, DebugFileResult debugFile, MethodDef targetMethod) {
			if (targetMethod.Module != targetModule)
				throw new InvalidOperationException();
			SetSourceModule(LoadModule(rawGeneratedModule, debugFile));

			var newMethod = FindSourceMethod(targetMethod);
			var newMethodNonNestedDeclType = newMethod.DeclaringType;
			while (newMethodNonNestedDeclType.DeclaringType != null)
				newMethodNonNestedDeclType = newMethodNonNestedDeclType.DeclaringType;

			AddEditedMethod(newMethod, targetMethod);
			if (!newMethodNonNestedDeclType.IsGlobalModuleType)
				AddGlobalTypeMembers(sourceModule.GlobalType);
			foreach (var type in sourceModule.Types) {
				if (type.IsGlobalModuleType)
					continue;
				if (type == newMethodNonNestedDeclType)
					continue;
				newNonNestedImportedTypes.Add(CreateNewImportedType(type, targetModule.Types));
			}
			InitializeTypes(oldTypeToNewType.Values.OfType<NewImportedType>());
			InitializeTypes(oldTypeToNewType.Values.OfType<MergedImportedType>());
			InitializeTypesMethods(oldTypeToNewType.Values.OfType<NewImportedType>());
			InitializeTypesMethods(oldTypeToNewType.Values.OfType<MergedImportedType>());
			UpdateEditedMethods();

			SetSourceModule(null);
		}

		MethodDef FindSourceMethod(MethodDef targetMethod) {
			var newType = sourceModule.Find(targetMethod.Module.Import(targetMethod.DeclaringType));
			if (newType == null)
				AddErrorThrow(IM0001, string.Format(dnSpy_AsmEditor_Resources.ERR_IM_CouldNotFindMethodType, targetMethod.DeclaringType));

			// Don't check type scopes or we won't be able to find methods with edited nested types.
			const SigComparerOptions comparerFlags = SIG_COMPARER_OPTIONS | SigComparerOptions.DontCompareTypeScope;

			var newMethod = newType.FindMethod(targetMethod.Name, targetMethod.MethodSig, comparerFlags, targetMethod.Module);
			if (newMethod != null)
				return newMethod;

			if (targetMethod.Overrides.Count != 0) {
				var targetOverriddenMethod = targetMethod.Overrides[0].MethodDeclaration;
				var comparer = new SigComparer(comparerFlags, targetModule);
				foreach (var method in newType.Methods) {
					foreach (var o in method.Overrides) {
						if (!comparer.Equals(o.MethodDeclaration, targetOverriddenMethod))
							continue;
						if (!comparer.Equals(o.MethodDeclaration.DeclaringType, targetOverriddenMethod.DeclaringType))
							continue;
						return method;
					}
				}
			}

			AddErrorThrow(IM0002, string.Format(dnSpy_AsmEditor_Resources.ERR_IM_CouldNotFindEditedMethod, targetMethod));
			throw new InvalidOperationException();
		}

		void SetSourceModule(ModuleDef newSourceModule) {
			this.sourceModule = newSourceModule;
			this.importSigComparerOptions = newSourceModule == null ? null : new ImportSigComparerOptions(newSourceModule, targetModule);
		}

		void UpdateEditedMethods() {
			foreach (var t in editedMethodsToFix) {
				var newMethod = t.Key;
				var targetMethod = t.Value;
				Debug.Assert(targetMethod.Module == targetModule);

				var importedType = (MergedImportedType)oldTypeToNewType[newMethod.DeclaringType];
				importedType.EditedMethodBodies.Add(new EditedMethodBody(targetMethod, newMethod.Body, newMethod.ImplAttributes));

				var body = newMethod.Body;
				if (body != null) {
					bodyDict.Clear();
					if (newMethod.Parameters.Count != targetMethod.Parameters.Count)
						throw new InvalidOperationException();
					for (int i = 0; i < newMethod.Parameters.Count; i++)
						bodyDict[newMethod.Parameters[i]] = targetMethod.Parameters[i];
					foreach (var instr in body.Instructions) {
						object newOp;
						if (instr.Operand != null && bodyDict.TryGetValue(instr.Operand, out newOp))
							instr.Operand = newOp;
					}
				}
			}
		}

		void AddEditedMethod(MethodDef newMethod, MethodDef targetMethod) {
			var newBaseType = newMethod.DeclaringType;
			var targetBaseType = targetMethod.DeclaringType;
			while (newBaseType.DeclaringType != null) {
				if (targetBaseType == null)
					throw new InvalidOperationException();
				newBaseType = newBaseType.DeclaringType;
				targetBaseType = targetBaseType.DeclaringType;
			}
			if (targetBaseType == null || targetBaseType.DeclaringType != null)
				throw new InvalidOperationException();

			nonNestedMergedImportedTypes.Add(AddUpdatedType(newBaseType, targetBaseType));
			editedMethodsToFix.Add(newMethod, targetMethod);
		}

		MergedImportedType AddUpdatedType(TypeDef newType, TypeDef targetType) {
			var importedType = AddMergedImportedType(newType, targetType, false);

			if (newType.NestedTypes.Count != 0 || targetType.NestedTypes.Count != 0) {
				var typeComparer = new TypeEqualityComparer(SigComparerOptions.DontCompareTypeScope);
				var newTypes = new Dictionary<TypeDef, TypeDef>(typeComparer);
				var targetTypes = new Dictionary<TypeDef, TypeDef>(typeComparer);
				foreach (var t in newType.NestedTypes)
					newTypes[t] = t;
				foreach (var t in targetType.NestedTypes)
					targetTypes[t] = t;
				if (newTypes.Count != newType.NestedTypes.Count)
					throw new InvalidOperationException();
				if (targetTypes.Count != targetType.NestedTypes.Count)
					throw new InvalidOperationException();

				foreach (var nestedTargetType in targetType.NestedTypes) {
					targetTypes.Remove(nestedTargetType);
					TypeDef nestedNewType;
					if (newTypes.TryGetValue(nestedTargetType, out nestedNewType)) {
						newTypes.Remove(nestedTargetType);
						var nestedImportedType = AddUpdatedType(nestedNewType, nestedTargetType);
						importedType.NewNestedTypes.Add(nestedImportedType);
					}
					else {
						// The user removed the type, or it was a compiler generated type that
						// was never shown in the decompiled code.
					}
				}
				// Whatever's left are types created by the user or the compiler
				foreach (var newNestedType in newTypes.Values)
					importedType.NewNestedTypes.Add(CreateNewImportedType(newNestedType, targetType.NestedTypes));
			}

			return importedType;
		}

		void AddGlobalTypeMembers(TypeDef newGlobalType) =>
			nonNestedMergedImportedTypes.Add(MergeTypes(newGlobalType, targetModule.GlobalType));

		// Adds every member as a new member. If a member exists in the target type, it's renamed.
		// Nested types aren't merged with existing nested types, they're just renamed.
		MergedImportedType MergeTypes(TypeDef newType, TypeDef targetType) {
			var mergedImportedType = AddMergedImportedType(newType, targetType, true);
			foreach (var nestedType in newType.NestedTypes) {
				var nestedImportedType = CreateNewImportedType(nestedType, targetType.NestedTypes);
				mergedImportedType.NewNestedTypes.Add(nestedImportedType);
			}
			return mergedImportedType;
		}

		static bool IsVirtual(PropertyDef property) => property.GetMethod?.IsVirtual == true || property.SetMethod?.IsVirtual == true;
		static bool IsVirtual(EventDef @event) => @event.AddMethod?.IsVirtual == true || @event.RemoveMethod?.IsVirtual == true || @event.InvokeMethod?.IsVirtual == true;

		void RenameMergedMembers(MergedImportedType mergedType) {
			if (!mergedType.RenameDuplicates)
				throw new InvalidOperationException();
			var existingProps = new HashSet<PropertyDef>(new ImportPropertyEqualityComparer(new ImportSigComparer(importSigComparerOptions, SIG_COMPARER_OPTIONS | SigComparerOptions.DontCompareReturnType, targetModule)));
			var existingMethods = new HashSet<MethodDef>(new ImportMethodEqualityComparer(new ImportSigComparer(importSigComparerOptions, SIG_COMPARER_OPTIONS | SigComparerOptions.DontCompareReturnType, targetModule)));
			var existingEventsFields = new HashSet<string>(StringComparer.Ordinal);
			var suggestedNames = new Dictionary<MethodDef, string>();

			foreach (var p in mergedType.TargetType.Properties)
				existingProps.Add(p);
			foreach (var e in mergedType.TargetType.Events)
				existingEventsFields.Add(e.Name);
			foreach (var m in mergedType.TargetType.Methods)
				existingMethods.Add(m);
			foreach (var f in mergedType.TargetType.Fields)
				existingEventsFields.Add(f.Name);

			var compiledType = toExtraData[mergedType].CompiledType;
			foreach (var compiledProp in compiledType.Properties) {
				var newProp = oldPropertyToNewProperty[compiledProp];
				if (!existingProps.Contains(newProp))
					continue;

				if (IsVirtual(compiledProp))
					AddError(IM0006, string.Format(dnSpy_AsmEditor_Resources.ERR_IM_RenamingVirtualPropsIsNotSupported, compiledProp));

				var origName = newProp.Name;
				int counter = 0;
				while (existingProps.Contains(newProp))
					newProp.Name = origName + "_" + (counter++).ToString();
				existingProps.Add(newProp);
				if (newProp.GetMethod != null)
					suggestedNames[newProp.GetMethod] = "get_" + newProp.Name;
				if (newProp.SetMethod != null)
					suggestedNames[newProp.SetMethod] = "set_" + newProp.Name;
			}

			foreach (var compiledEvent in compiledType.Events) {
				var newEvent = oldEventToNewEvent[compiledEvent];
				if (!existingEventsFields.Contains(newEvent.Name))
					continue;

				if (IsVirtual(compiledEvent))
					AddError(IM0007, string.Format(dnSpy_AsmEditor_Resources.ERR_IM_RenamingVirtualEventsIsNotSupported, compiledEvent));

				var origName = newEvent.Name;
				int counter = 0;
				while (existingEventsFields.Contains(newEvent.Name))
					newEvent.Name = origName + "_" + (counter++).ToString();
				existingEventsFields.Add(newEvent.Name);
				if (newEvent.AddMethod != null)
					suggestedNames[newEvent.AddMethod] = "add_" + newEvent.Name;
				if (newEvent.RemoveMethod != null)
					suggestedNames[newEvent.RemoveMethod] = "remove_" + newEvent.Name;
				if (newEvent.InvokeMethod != null)
					suggestedNames[newEvent.InvokeMethod] = "raise_" + newEvent.Name;
			}

			foreach (var compiledMethod in compiledType.Methods) {
				var newMethod = oldMethodToNewMethod[compiledMethod];

				string suggestedName;
				suggestedNames.TryGetValue(newMethod, out suggestedName);

				if (suggestedName == null && !existingMethods.Contains(newMethod))
					continue;

				if (compiledMethod.IsVirtual)
					AddError(IM0008, string.Format(dnSpy_AsmEditor_Resources.ERR_IM_RenamingVirtualMethodsIsNotSupported, compiledMethod));

				string baseName = suggestedName ?? newMethod.Name;
				int counter = 0;
				newMethod.Name = baseName;
				while (existingMethods.Contains(newMethod))
					newMethod.Name = baseName + "_" + (counter++).ToString();
				existingMethods.Add(newMethod);
			}

			foreach (var compiledField in compiledType.Fields) {
				var newField = oldFieldToNewField[compiledField];
				if (!existingEventsFields.Contains(newField.Name))
					continue;

				var origName = newField.Name;
				int counter = 0;
				while (existingEventsFields.Contains(newField.Name))
					newField.Name = origName + "_" + (counter++).ToString();
				existingEventsFields.Add(newField.Name);
			}
		}

		NewImportedType CreateNewImportedType(TypeDef newType, IList<TypeDef> existingTypes) {
			var name = GetNewName(newType, existingTypes);
			var importedType = AddNewImportedType(newType, name);
			AddNewNestedTypes(newType);
			return importedType;
		}

		void AddNewNestedTypes(TypeDef newType) {
			if (newType.NestedTypes.Count == 0)
				return;
			var stack = new Stack<TypeDef>();
			foreach (var t in newType.NestedTypes)
				stack.Push(t);
			while (stack.Count > 0) {
				var newNestedType = stack.Pop();
				var importedNewNestedType = AddNewImportedType(newNestedType, newNestedType.Name);
				foreach (var t in newNestedType.NestedTypes)
					stack.Push(t);
			}
		}

		static UTF8String GetNewName(TypeDef type, IList<TypeDef> otherTypes) {
			var name = type.Name;
			int counter = 0;
			while ((otherTypes.Any(a => a.Name == name && a.Namespace == type.Namespace))) {
				// It's prepended because generic types have a `<number> appended to the name,
				// which they still should have after the rename.
				name = "__" + (counter++).ToString() + "__" + type.Name.String;
			}
			return name;
		}

		MergedImportedType AddMergedImportedType(TypeDef type, TypeDef origTypeToKeep, bool renameDuplicates) {
			var importedType = new MergedImportedType(origTypeToKeep, renameDuplicates);
			toExtraData.Add(importedType, new ExtraImportedTypeData(type));
			oldTypeToNewType.Add(type, importedType);
			if (!type.IsGlobalModuleType)
				oldTypeRefToNewType.Add(type, importedType);
			return importedType;
		}

		NewImportedType AddNewImportedType(TypeDef type, UTF8String name) {
			var createdType = targetModule.UpdateRowId(new TypeDefUser(type.Namespace, name) { Attributes = type.Attributes });
			var importedType = new NewImportedType(createdType);
			toExtraData.Add(importedType, new ExtraImportedTypeData(type));
			oldTypeToNewType.Add(type, importedType);
			oldTypeRefToNewType.Add(type, importedType);
			return importedType;
		}

		void InitializeTypes(IEnumerable<NewImportedType> importedTypes) {
			foreach (var importedType in importedTypes) {
				var compiledType = toExtraData[importedType].CompiledType;
				importedType.TargetType.BaseType = Import(compiledType.BaseType);
				ImportCustomAttributes(importedType.TargetType, compiledType);
				ImportDeclSecurities(importedType.TargetType, compiledType);
				importedType.TargetType.ClassLayout = Import(compiledType.ClassLayout);
				foreach (var genericParam in compiledType.GenericParameters)
					importedType.TargetType.GenericParameters.Add(Import(genericParam));
				foreach (var iface in compiledType.Interfaces)
					importedType.TargetType.Interfaces.Add(Import(iface));
				foreach (var nestedType in compiledType.NestedTypes)
					importedType.TargetType.NestedTypes.Add(oldTypeToNewType[nestedType].TargetType);

				foreach (var field in compiledType.Fields)
					importedType.TargetType.Fields.Add(Import(field));
				foreach (var method in compiledType.Methods)
					importedType.TargetType.Methods.Add(Import(method));

				// Import the properties and events after the methods have been created
				foreach (var prop in compiledType.Properties)
					importedType.TargetType.Properties.Add(Import(prop));
				foreach (var evt in compiledType.Events)
					importedType.TargetType.Events.Add(Import(evt));
			}
		}

		void InitializeTypes(IEnumerable<MergedImportedType> importedTypes) {
			var memberDict = new MemberLookup(new ImportSigComparer(importSigComparerOptions, 0, targetModule));

			foreach (var importedType in importedTypes) {
				var compiledType = toExtraData[importedType].CompiledType;

				if (importedType.RenameDuplicates) {
					// All dupes are assumed to be new members and they're all renamed

					foreach (var field in compiledType.Fields)
						importedType.NewFields.Add(Import(field));
					foreach (var method in compiledType.Methods)
						importedType.NewMethods.Add(Import(method));

					// Import the properties and events after the methods have been created
					foreach (var prop in compiledType.Properties)
						importedType.NewProperties.Add(Import(prop));
					foreach (var evt in compiledType.Events)
						importedType.NewEvents.Add(Import(evt));

					RenameMergedMembers(importedType);
				}
				else {
					// Duplicate members are assumed to be original members (stubs) and
					// we should just redirect refs to them to the original members.

					memberDict.Initialize(importedType.TargetType);

					foreach (var compiledField in compiledType.Fields) {
						FieldDef targetField;
						if ((targetField = memberDict.FindField(compiledField)) != null) {
							memberDict.Remove(targetField);
							isStub.Add(compiledField);
							isStub.Add(targetField);
							oldFieldToNewField.Add(compiledField, targetField);
						}
						else
							importedType.NewFields.Add(Import(compiledField));
					}
					foreach (var compiledMethod in compiledType.Methods) {
						MethodDef targetMethod;
						if ((targetMethod = memberDict.FindMethod(compiledMethod)) != null || editedMethodsToFix.TryGetValue(compiledMethod, out targetMethod)) {
							memberDict.Remove(targetMethod);
							isStub.Add(compiledMethod);
							isStub.Add(targetMethod);
							oldMethodToNewMethod.Add(compiledMethod, targetMethod);
						}
						else
							importedType.NewMethods.Add(Import(compiledMethod));
					}

					// Import the properties and events after the methods have been created
					foreach (var compiledProp in compiledType.Properties) {
						PropertyDef targetProp;
						if ((targetProp = memberDict.FindProperty(compiledProp)) != null) {
							memberDict.Remove(targetProp);
							isStub.Add(compiledProp);
							isStub.Add(targetProp);
							oldPropertyToNewProperty.Add(compiledProp, targetProp);
						}
						else
							importedType.NewProperties.Add(Import(compiledProp));
					}
					foreach (var compiledEvent in compiledType.Events) {
						EventDef targetEvent;
						if ((targetEvent = memberDict.FindEvent(compiledEvent)) != null) {
							memberDict.Remove(targetEvent);
							isStub.Add(compiledEvent);
							isStub.Add(targetEvent);
							oldEventToNewEvent.Add(compiledEvent, targetEvent);
						}
						else
							importedType.NewEvents.Add(Import(compiledEvent));
					}
				}
			}
		}

		void InitializeTypesMethods(IEnumerable<NewImportedType> importedTypes) {
			foreach (var importedType in importedTypes) {
				foreach (var compiledMethod in toExtraData[importedType].CompiledType.Methods) {
					var targetMethod = oldMethodToNewMethod[compiledMethod];
					foreach (var o in compiledMethod.Overrides)
						targetMethod.Overrides.Add(Import(o));
					InitializeBody(targetMethod, compiledMethod);
				}
			}
		}

		void InitializeTypesMethods(IEnumerable<MergedImportedType> importedTypes) {
			foreach (var importedType in importedTypes) {
				foreach (var compiledMethod in toExtraData[importedType].CompiledType.Methods) {
					var targetMethod = oldMethodToNewMethod[compiledMethod];
					MethodDef targetMethod2;
					if (editedMethodsToFix.TryGetValue(compiledMethod, out targetMethod2)) {
						if (targetMethod != targetMethod2)
							throw new InvalidOperationException();
						if (compiledMethod.IsStatic != targetMethod.IsStatic) {
							// The method signature and method attributes (the IsStatic flag) would need to be
							// updated too, but we only update the method body and the impl attributes.
							AddError(IM0009, string.Format(dnSpy_AsmEditor_Resources.ERR_IM_AddingRemovingStaticFromEditedMethodNotSupported, targetMethod));
						}
						InitializeBody(compiledMethod, compiledMethod);
					}
					else if (!isStub.Contains(targetMethod)) {
						foreach (var o in compiledMethod.Overrides)
							targetMethod.Overrides.Add(Import(o));
						InitializeBody(targetMethod, compiledMethod);
					}
				}
			}
		}

		MethodOverride Import(MethodOverride o) => new MethodOverride(Import(o.MethodBody), Import(o.MethodDeclaration));
		IMethodDefOrRef Import(IMethodDefOrRef method) => (IMethodDefOrRef)Import((IMethod)method);

		ITypeDefOrRef Import(ITypeDefOrRef type) {
			if (type == null)
				return null;

			ImportedType importedType;
			var res = TryGetTypeInTargetModule(type, out importedType);
			if (res != null)
				return res;

			var tr = type as TypeRef;
			if (tr != null)
				return ImportTypeRefNoModuleChecks(tr, 0);

			var ts = type as TypeSpec;
			if (ts != null)
				return ImportTypeSpec(ts);

			// TypeDefs are already handled elsewhere
			throw new InvalidOperationException();
		}

		TypeRef ImportTypeRefNoModuleChecks(TypeRef tr, int recurseCount) {
			const int MAX_RECURSE_COUNT = 500;
			if (recurseCount >= MAX_RECURSE_COUNT)
				return null;

			var scope = tr.ResolutionScope;
			IResolutionScope importedScope;

			var scopeTypeRef = scope as TypeRef;
			if (scopeTypeRef != null)
				importedScope = ImportTypeRefNoModuleChecks(scopeTypeRef, recurseCount + 1);
			else if (scope is AssemblyRef)
				importedScope = Import((AssemblyRef)scope);
			else if (scope is ModuleRef)
				importedScope = Import((ModuleRef)scope, true);
			else if (scope is ModuleDef) {
				if (scope == targetModule || scope == sourceModule)
					importedScope = targetModule;
				else
					throw new InvalidOperationException();
			}
			else
				throw new InvalidOperationException();

			var importedTypeRef = targetModule.UpdateRowId(new TypeRefUser(targetModule, tr.Namespace, tr.Name, importedScope));
			ImportCustomAttributes(importedTypeRef, tr);
			return importedTypeRef;
		}

		AssemblyRef Import(AssemblyRef asmRef) {
			if (asmRef == null)
				return null;
			var importedAssemblyRef = targetModule.UpdateRowId(new AssemblyRefUser(asmRef.Name, asmRef.Version, asmRef.PublicKeyOrToken, asmRef.Culture));
			ImportCustomAttributes(importedAssemblyRef, asmRef);
			importedAssemblyRef.Attributes = asmRef.Attributes;
			importedAssemblyRef.Hash = asmRef.Hash;
			return importedAssemblyRef;
		}

		TypeSpec ImportTypeSpec(TypeSpec ts) {
			if (ts == null)
				return null;
			var importedTypeSpec = targetModule.UpdateRowId(new TypeSpecUser(Import(ts.TypeSig)));
			ImportCustomAttributes(importedTypeSpec, ts);
			importedTypeSpec.ExtraData = ts.ExtraData;
			return importedTypeSpec;
		}

		TypeDef TryGetTypeInTargetModule(ITypeDefOrRef tdr, out ImportedType importedType) {
			if (tdr == null) {
				importedType = null;
				return null;
			}

			var td = tdr as TypeDef;
			if (td != null)
				return (importedType = oldTypeToNewType[td]).TargetType;

			var tr = tdr as TypeRef;
			if (tr != null) {
				ImportedType importedTypeTmp;
				if (oldTypeRefToNewType.TryGetValue(tr, out importedTypeTmp))
					return (importedType = importedTypeTmp).TargetType;

				var tr2 = (TypeRef)tr.GetNonNestedTypeRefScope();
				if (IsTarget(tr2.ResolutionScope)) {
					td = targetModule.Find(tr);
					if (td != null) {
						importedType = null;
						return td;
					}

					AddError(IM0003, string.Format(dnSpy_AsmEditor_Resources.ERR_IM_CouldNotFindType, tr));
					importedType = null;
					return null;
				}
				if (IsSource(tr2.ResolutionScope))
					throw new InvalidOperationException();
			}

			importedType = null;
			return null;
		}

		bool IsSourceOrTarget(IResolutionScope scope) => IsSource(scope) || IsTarget(scope);

		bool IsSource(IResolutionScope scope) {
			var asmRef = scope as AssemblyRef;
			if (asmRef != null)
				return IsSource(asmRef);

			var modRef = scope as ModuleRef;
			if (modRef != null)
				return IsSource(modRef);

			return scope == sourceModule;
		}

		bool IsTarget(IResolutionScope scope) {
			var asmRef = scope as AssemblyRef;
			if (asmRef != null)
				return IsTarget(asmRef);

			var modRef = scope as ModuleRef;
			if (modRef != null)
				return IsTarget(modRef);

			return scope == targetModule;
		}

		bool IsSourceOrTarget(AssemblyRef asmRef) => IsSource(asmRef) || IsTarget(asmRef);
		bool IsSource(AssemblyRef asmRef) => AssemblyNameComparer.CompareAll.Equals(asmRef, sourceModule.Assembly);
		bool IsTarget(AssemblyRef asmRef) => AssemblyNameComparer.CompareAll.Equals(asmRef, targetModule.Assembly);

		bool IsSourceOrTarget(ModuleRef modRef) => IsSource(modRef) || IsTarget(modRef);
		bool IsSource(ModuleRef modRef) => StringComparer.OrdinalIgnoreCase.Equals(modRef?.Name, sourceModule.Name);
		bool IsTarget(ModuleRef modRef) => StringComparer.OrdinalIgnoreCase.Equals(modRef?.Name, targetModule.Name);

		TypeDef ImportTypeDef(TypeDef type) => type == null ? null : oldTypeToNewType[type].TargetType;
		MethodDef ImportMethodDef(MethodDef method) => method == null ? null : oldMethodToNewMethod[method];

		TypeSig Import(TypeSig type) {
			if (type == null)
				return null;

			TypeSig result;
			switch (type.ElementType) {
			case ElementType.Void:		result = targetModule.CorLibTypes.Void; break;
			case ElementType.Boolean:	result = targetModule.CorLibTypes.Boolean; break;
			case ElementType.Char:		result = targetModule.CorLibTypes.Char; break;
			case ElementType.I1:		result = targetModule.CorLibTypes.SByte; break;
			case ElementType.U1:		result = targetModule.CorLibTypes.Byte; break;
			case ElementType.I2:		result = targetModule.CorLibTypes.Int16; break;
			case ElementType.U2:		result = targetModule.CorLibTypes.UInt16; break;
			case ElementType.I4:		result = targetModule.CorLibTypes.Int32; break;
			case ElementType.U4:		result = targetModule.CorLibTypes.UInt32; break;
			case ElementType.I8:		result = targetModule.CorLibTypes.Int64; break;
			case ElementType.U8:		result = targetModule.CorLibTypes.UInt64; break;
			case ElementType.R4:		result = targetModule.CorLibTypes.Single; break;
			case ElementType.R8:		result = targetModule.CorLibTypes.Double; break;
			case ElementType.String:	result = targetModule.CorLibTypes.String; break;
			case ElementType.TypedByRef:result = targetModule.CorLibTypes.TypedReference; break;
			case ElementType.I:			result = targetModule.CorLibTypes.IntPtr; break;
			case ElementType.U:			result = targetModule.CorLibTypes.UIntPtr; break;
			case ElementType.Object:	result = targetModule.CorLibTypes.Object; break;
			case ElementType.Ptr:		result = new PtrSig(Import(type.Next)); break;
			case ElementType.ByRef:		result = new ByRefSig(Import(type.Next)); break;
			case ElementType.ValueType: result = CreateClassOrValueType((type as ClassOrValueTypeSig).TypeDefOrRef, true); break;
			case ElementType.Class:		result = CreateClassOrValueType((type as ClassOrValueTypeSig).TypeDefOrRef, false); break;
			case ElementType.Var:		result = new GenericVar((type as GenericVar).Number, ImportTypeDef((type as GenericVar).OwnerType)); break;
			case ElementType.ValueArray:result = new ValueArraySig(Import(type.Next), (type as ValueArraySig).Size); break;
			case ElementType.FnPtr:		result = new FnPtrSig(Import((type as FnPtrSig).Signature)); break;
			case ElementType.SZArray:	result = new SZArraySig(Import(type.Next)); break;
			case ElementType.MVar:		result = new GenericMVar((type as GenericMVar).Number, ImportMethodDef((type as GenericMVar).OwnerMethod)); break;
			case ElementType.CModReqd:	result = new CModReqdSig(Import((type as ModifierSig).Modifier), Import(type.Next)); break;
			case ElementType.CModOpt:	result = new CModOptSig(Import((type as ModifierSig).Modifier), Import(type.Next)); break;
			case ElementType.Module:	result = new ModuleSig((type as ModuleSig).Index, Import(type.Next)); break;
			case ElementType.Sentinel:	result = new SentinelSig(); break;
			case ElementType.Pinned:	result = new PinnedSig(Import(type.Next)); break;

			case ElementType.Array:
				var arraySig = (ArraySig)type;
				var sizes = new List<uint>(arraySig.Sizes);
				var lbounds = new List<int>(arraySig.LowerBounds);
				result = new ArraySig(Import(type.Next), arraySig.Rank, sizes, lbounds);
				break;

			case ElementType.GenericInst:
				var gis = (GenericInstSig)type;
				var genArgs = new List<TypeSig>(gis.GenericArguments.Count);
				foreach (var ga in gis.GenericArguments)
					genArgs.Add(Import(ga));
				result = new GenericInstSig(Import(gis.GenericType) as ClassOrValueTypeSig, genArgs);
				break;

			case ElementType.End:
			case ElementType.R:
			case ElementType.Internal:
			default:
				result = null;
				break;
			}

			return result;
		}

		TypeSig CreateClassOrValueType(ITypeDefOrRef type, bool isValueType) {
			var corLibType = targetModule.CorLibTypes.GetCorLibTypeSig(type);
			if (corLibType != null)
				return corLibType;

			if (isValueType)
				return new ValueTypeSig(Import(type));
			return new ClassSig(Import(type));
		}

		void ImportCustomAttributes(IHasCustomAttribute target, IHasCustomAttribute source) {
			foreach (var ca in source.CustomAttributes)
				target.CustomAttributes.Add(Import(ca));
		}

		CustomAttribute Import(CustomAttribute ca) {
			if (ca == null)
				return null;
			if (ca.IsRawBlob)
				return new CustomAttribute(ca.Constructor, ca.RawData);

			var importedCustomAttribute = new CustomAttribute(ca.Constructor);
			foreach (var arg in ca.ConstructorArguments)
				importedCustomAttribute.ConstructorArguments.Add(Import(arg));
			foreach (var namedArg in ca.NamedArguments)
				importedCustomAttribute.NamedArguments.Add(Import(namedArg));

			return importedCustomAttribute;
		}

		CAArgument Import(CAArgument arg) => new CAArgument(Import(arg.Type), ImportCAValue(arg.Value));

		object ImportCAValue(object value) {
			if (value is CAArgument)
				return Import((CAArgument)value);
			if (value is IList<CAArgument>) {
				var args = (IList<CAArgument>)value;
				var newArgs = ThreadSafeListCreator.Create<CAArgument>(args.Count);
				foreach (var arg in args)
					newArgs.Add(Import(arg));
				return newArgs;
			}
			if (value is TypeSig)
				return Import((TypeSig)value);
			return value;
		}

		CANamedArgument Import(CANamedArgument namedArg) =>
			new CANamedArgument(namedArg.IsField, Import(namedArg.Type), namedArg.Name, Import(namedArg.Argument));

		void ImportDeclSecurities(IHasDeclSecurity target, IHasDeclSecurity source) {
			foreach (var ds in source.DeclSecurities)
				target.DeclSecurities.Add(Import(ds));
		}

		DeclSecurity Import(DeclSecurity ds) {
			if (ds == null)
				return null;

			var importedDeclSecurity = targetModule.UpdateRowId(new DeclSecurityUser());
			ImportCustomAttributes(importedDeclSecurity, ds);
			importedDeclSecurity.Action = ds.Action;
			foreach (var sa in ds.SecurityAttributes)
				importedDeclSecurity.SecurityAttributes.Add(Import(sa));

			return importedDeclSecurity;
		}

		SecurityAttribute Import(SecurityAttribute sa) {
			if (sa == null)
				return null;

			var importedSecurityAttribute = new SecurityAttribute(Import(sa.AttributeType));
			foreach (var namedArg in sa.NamedArguments)
				importedSecurityAttribute.NamedArguments.Add(Import(namedArg));

			return importedSecurityAttribute;
		}

		Constant Import(Constant constant) {
			if (constant == null)
				return null;
			return targetModule.UpdateRowId(new ConstantUser(constant.Value, constant.Type));
		}

		MarshalType Import(MarshalType marshalType) {
			if (marshalType == null)
				return null;

			if (marshalType is RawMarshalType) {
				var mt = (RawMarshalType)marshalType;
				return new RawMarshalType(mt.Data);
			}

			if (marshalType is FixedSysStringMarshalType) {
				var mt = (FixedSysStringMarshalType)marshalType;
				return new FixedSysStringMarshalType(mt.Size);
			}

			if (marshalType is SafeArrayMarshalType) {
				var mt = (SafeArrayMarshalType)marshalType;
				return new SafeArrayMarshalType(mt.VariantType, Import(mt.UserDefinedSubType));
			}

			if (marshalType is FixedArrayMarshalType) {
				var mt = (FixedArrayMarshalType)marshalType;
				return new FixedArrayMarshalType(mt.Size, mt.ElementType);
			}

			if (marshalType is ArrayMarshalType) {
				var mt = (ArrayMarshalType)marshalType;
				return new ArrayMarshalType(mt.ElementType, mt.ParamNumber, mt.Size, mt.Flags);
			}

			if (marshalType is CustomMarshalType) {
				var mt = (CustomMarshalType)marshalType;
				return new CustomMarshalType(mt.Guid, mt.NativeTypeName, Import(mt.CustomMarshaler), mt.Cookie);
			}

			if (marshalType is InterfaceMarshalType) {
				var mt = (InterfaceMarshalType)marshalType;
				return new InterfaceMarshalType(mt.NativeType, mt.IidParamIndex);
			}

			Debug.Assert(marshalType.GetType() == typeof(MarshalType));
			return new MarshalType(marshalType.NativeType);
		}

		ImplMap Import(ImplMap implMap) {
			if (implMap == null)
				return null;
			return targetModule.UpdateRowId(new ImplMapUser(Import(implMap.Module, false), implMap.Name, implMap.Attributes));
		}

		ModuleRef Import(ModuleRef module, bool canConvertToTargetModule) {
			var name = canConvertToTargetModule && IsSourceOrTarget(module) ? targetModule.Name : module.Name;
			var importedModuleRef = targetModule.UpdateRowId(new ModuleRefUser(targetModule, name));
			ImportCustomAttributes(importedModuleRef, module);
			return importedModuleRef;
		}

		ClassLayout Import(ClassLayout classLayout) {
			if (classLayout == null)
				return null;
			return targetModule.UpdateRowId(new ClassLayoutUser(classLayout.PackingSize, classLayout.ClassSize));
		}

		CallingConventionSig Import(CallingConventionSig signature) {
			if (signature == null)
				return null;

			if (signature is MethodSig)
				return Import((MethodSig)signature);
			if (signature is FieldSig)
				return Import((FieldSig)signature);
			if (signature is GenericInstMethodSig)
				return Import((GenericInstMethodSig)signature);
			if (signature is PropertySig)
				return Import((PropertySig)signature);
			if (signature is LocalSig)
				return Import((LocalSig)signature);
			return null;
		}

		MethodSig Import(MethodSig sig) {
			if (sig == null)
				return null;
			return Import(new MethodSig(sig.GetCallingConvention()), sig);
		}

		PropertySig Import(PropertySig sig) {
			if (sig == null)
				return null;
			return Import(new PropertySig(sig.HasThis), sig);
		}

		T Import<T>(T sig, T old) where T : MethodBaseSig {
			sig.RetType = Import(old.RetType);
			foreach (var p in old.Params)
				sig.Params.Add(Import(p));
			sig.GenParamCount = old.GenParamCount;
			var paramsAfterSentinel = sig.ParamsAfterSentinel;
			if (paramsAfterSentinel != null) {
				foreach (var p in old.ParamsAfterSentinel)
					paramsAfterSentinel.Add(Import(p));
			}
			return sig;
		}

		FieldSig Import(FieldSig sig) {
			if (sig == null)
				return null;
			return new FieldSig(Import(sig.Type));
		}

		GenericInstMethodSig Import(GenericInstMethodSig sig) {
			if (sig == null)
				return null;

			var result = new GenericInstMethodSig();
			foreach (var l in sig.GenericArguments)
				result.GenericArguments.Add(Import(l));

			return result;
		}

		LocalSig Import(LocalSig sig) {
			if (sig == null)
				return null;

			var result = new LocalSig();
			foreach (var l in sig.Locals)
				result.Locals.Add(Import(l));

			return result;
		}

		static readonly bool keepImportedRva = false;
		RVA GetRVA(RVA rva) => keepImportedRva ? rva : 0;

		FieldDef Import(FieldDef field) {
			if (field == null)
				return null;
			var importedField = targetModule.UpdateRowId(new FieldDefUser(field.Name));
			oldFieldToNewField.Add(field, importedField);
			ImportCustomAttributes(importedField, field);
			importedField.Signature = Import(field.Signature);
			importedField.Attributes = field.Attributes;
			importedField.RVA = GetRVA(field.RVA);
			importedField.InitialValue = field.InitialValue;
			importedField.Constant = Import(field.Constant);
			importedField.FieldOffset = field.FieldOffset;
			importedField.MarshalType = Import(field.MarshalType);
			importedField.ImplMap = Import(field.ImplMap);
			return importedField;
		}

		MethodDef Import(MethodDef method) {
			if (method == null)
				return null;
			var importedMethodDef = targetModule.UpdateRowId(new MethodDefUser(method.Name));
			oldMethodToNewMethod.Add(method, importedMethodDef);
			ImportCustomAttributes(importedMethodDef, method);
			ImportDeclSecurities(importedMethodDef, method);
			importedMethodDef.RVA = GetRVA(method.RVA);
			importedMethodDef.ImplAttributes = method.ImplAttributes;
			importedMethodDef.Attributes = method.Attributes;
			importedMethodDef.Signature = Import(method.Signature);
			importedMethodDef.SemanticsAttributes = method.SemanticsAttributes;
			importedMethodDef.ImplMap = Import(method.ImplMap);
			foreach (var paramDef in method.ParamDefs)
				importedMethodDef.ParamDefs.Add(Import(paramDef));
			foreach (var genericParam in method.GenericParameters)
				importedMethodDef.GenericParameters.Add(Import(genericParam));
			importedMethodDef.Parameters.UpdateParameterTypes();
			return importedMethodDef;
		}

		GenericParam Import(GenericParam gp) {
			if (gp == null)
				return null;
			var importedGenericParam = targetModule.UpdateRowId(new GenericParamUser(gp.Number, gp.Flags, gp.Name));
			ImportCustomAttributes(importedGenericParam, gp);
			importedGenericParam.Kind = Import(gp.Kind);
			foreach (var gpc in gp.GenericParamConstraints)
				importedGenericParam.GenericParamConstraints.Add(Import(gpc));
			return importedGenericParam;
		}

		GenericParamConstraint Import(GenericParamConstraint gpc) {
			if (gpc == null)
				return null;
			var importedGenericParamConstraint = targetModule.UpdateRowId(new GenericParamConstraintUser(Import(gpc.Constraint)));
			ImportCustomAttributes(importedGenericParamConstraint, gpc);
			return importedGenericParamConstraint;
		}

		InterfaceImpl Import(InterfaceImpl ifaceImpl) {
			if (ifaceImpl == null)
				return null;
			var importedInterfaceImpl = targetModule.UpdateRowId(new InterfaceImplUser(Import(ifaceImpl.Interface)));
			ImportCustomAttributes(importedInterfaceImpl, ifaceImpl);
			return importedInterfaceImpl;
		}

		ParamDef Import(ParamDef paramDef) {
			if (paramDef == null)
				return null;
			var importedParamDef = targetModule.UpdateRowId(new ParamDefUser(paramDef.Name, paramDef.Sequence, paramDef.Attributes));
			ImportCustomAttributes(importedParamDef, paramDef);
			importedParamDef.MarshalType = Import(paramDef.MarshalType);
			importedParamDef.Constant = Import(paramDef.Constant);
			return importedParamDef;
		}

		PropertyDef Import(PropertyDef propDef) {
			if (propDef == null)
				return null;
			var importedPropertyDef = targetModule.UpdateRowId(new PropertyDefUser(propDef.Name));
			oldPropertyToNewProperty[propDef] = importedPropertyDef;
			ImportCustomAttributes(importedPropertyDef, propDef);
			importedPropertyDef.Attributes = propDef.Attributes;
			importedPropertyDef.Type = Import(propDef.Type);
			importedPropertyDef.Constant = Import(propDef.Constant);
			foreach (var m in propDef.GetMethods)
				importedPropertyDef.GetMethods.Add(oldMethodToNewMethod[m]);
			foreach (var m in propDef.SetMethods)
				importedPropertyDef.SetMethods.Add(oldMethodToNewMethod[m]);
			foreach (var m in propDef.OtherMethods)
				importedPropertyDef.OtherMethods.Add(oldMethodToNewMethod[m]);
			return importedPropertyDef;
		}

		EventDef Import(EventDef eventDef) {
			if (eventDef == null)
				return null;
			var importedEventDef = targetModule.UpdateRowId(new EventDefUser(eventDef.Name, Import(eventDef.EventType), eventDef.Attributes));
			oldEventToNewEvent[eventDef] = importedEventDef;
			ImportCustomAttributes(importedEventDef, eventDef);
			if (eventDef.AddMethod != null)
				importedEventDef.AddMethod = oldMethodToNewMethod[eventDef.AddMethod];
			if (eventDef.InvokeMethod != null)
				importedEventDef.InvokeMethod = oldMethodToNewMethod[eventDef.InvokeMethod];
			if (eventDef.RemoveMethod != null)
				importedEventDef.RemoveMethod = oldMethodToNewMethod[eventDef.RemoveMethod];
			foreach (var m in eventDef.OtherMethods)
				importedEventDef.OtherMethods.Add(oldMethodToNewMethod[m]);
			return importedEventDef;
		}

		void InitializeBody(MethodDef targetMethod, MethodDef sourceMethod) {
			// NOTE: Both methods can be identical: targetMethod == sourceMethod

			var sourceBody = sourceMethod.Body;
			if (sourceBody == null) {
				targetMethod.Body = null;
				return;
			}

			var targetBody = new CilBody();
			targetMethod.Body = targetBody;
			targetBody.KeepOldMaxStack = sourceBody.KeepOldMaxStack;
			targetBody.InitLocals = sourceBody.InitLocals;
			targetBody.HeaderSize = sourceBody.HeaderSize;
			targetBody.MaxStack = sourceBody.MaxStack;
			targetBody.LocalVarSigTok = sourceBody.LocalVarSigTok;

			bodyDict.Clear();
			foreach (var local in sourceBody.Variables) {
				var newLocal = new Local(Import(local.Type), local.Name);
				bodyDict[local] = newLocal;
				newLocal.PdbAttributes = local.PdbAttributes;
				targetBody.Variables.Add(newLocal);
			}

			int si = sourceMethod.IsStatic ? 0 : 1;
			int ti = targetMethod.IsStatic ? 0 : 1;
			if (sourceMethod.Parameters.Count - si != targetMethod.Parameters.Count - ti)
				throw new InvalidOperationException();
			for (; si < sourceMethod.Parameters.Count && ti < targetMethod.Parameters.Count; si++, ti++)
				bodyDict[sourceMethod.Parameters[si]] = targetMethod.Parameters[ti];

			foreach (var instr in sourceBody.Instructions) {
				var newInstr = new Instruction(instr.OpCode, instr.Operand);
				newInstr.Offset = instr.Offset;
				newInstr.SequencePoint = instr.SequencePoint?.Clone();
				bodyDict[instr] = newInstr;
				targetBody.Instructions.Add(newInstr);
			}

			foreach (var eh in sourceBody.ExceptionHandlers) {
				var newEh = new ExceptionHandler(eh.HandlerType);
				newEh.TryStart = GetInstruction(bodyDict, eh.TryStart);
				newEh.TryEnd = GetInstruction(bodyDict, eh.TryEnd);
				newEh.FilterStart = GetInstruction(bodyDict, eh.FilterStart);
				newEh.HandlerStart = GetInstruction(bodyDict, eh.HandlerStart);
				newEh.HandlerEnd = GetInstruction(bodyDict, eh.HandlerEnd);
				newEh.CatchType = Import(eh.CatchType);
				targetBody.ExceptionHandlers.Add(newEh);
			}

			foreach (var newInstr in targetBody.Instructions) {
				var op = newInstr.Operand;
				if (op == null)
					continue;

				object obj;
				if (bodyDict.TryGetValue(op, out obj)) {
					newInstr.Operand = obj;
					continue;
				}

				var oldList = op as IList<Instruction>;
				if (oldList != null) {
					var targets = new Instruction[oldList.Count];
					for (int i = 0; i < oldList.Count; i++)
						targets[i] = GetInstruction(bodyDict, oldList[i]);
					newInstr.Operand = targets;
					continue;
				}

				var tdr = op as ITypeDefOrRef;
				if (tdr != null) {
					newInstr.Operand = Import(tdr);
					continue;
				}

				var method = op as IMethod;
				if (method != null && method.IsMethod) {
					newInstr.Operand = Import(method);
					continue;
				}

				var field = op as IField;
				if (field != null) {
					newInstr.Operand = Import(field);
					continue;
				}

				var msig = op as MethodSig;
				if (msig != null) {
					newInstr.Operand = Import(msig);
					continue;
				}

				Debug.Assert(op is sbyte || op is byte || op is int || op is long || op is float || op is double || op is string);
			}
		}

		static Instruction GetInstruction(Dictionary<object, object> dict, Instruction instr) {
			object obj;
			if (instr == null || !dict.TryGetValue(instr, out obj))
				return null;
			return (Instruction)obj;
		}

		IMethod Import(IMethod method) {
			if (method == null)
				return null;

			var md = method as MethodDef;
			if (md != null)
				return oldMethodToNewMethod[md];

			var ms = method as MethodSpec;
			if (ms != null) {
				var importedMethodSpec = new MethodSpecUser(Import(ms.Method), Import(ms.GenericInstMethodSig));
				ImportCustomAttributes(importedMethodSpec, ms);
				return importedMethodSpec;
			}

			var mr = (MemberRef)method;
			ImportedType importedType;
			var td = TryGetTypeInTargetModule(mr.Class as ITypeDefOrRef, out importedType);
			if (td != null) {
				var targetMethod = FindMethod(td, mr);
				if (targetMethod != null)
					return targetMethod;
				if (importedType != null) {
					var compiledMethod = FindMethod(toExtraData[importedType].CompiledType, mr);
					if (compiledMethod != null)
						return oldMethodToNewMethod[compiledMethod];
				}

				AddError(IM0004, string.Format(dnSpy_AsmEditor_Resources.ERR_IM_CouldNotFindMethod, mr));
				return null;
			}

			return ImportNoCheckForDefs(mr);
		}

		MethodDef FindMethod(TypeDef targetType, MemberRef mr) {
			var comparer = new ImportSigComparer(importSigComparerOptions, SIG_COMPARER_OPTIONS, targetModule);
			foreach (var method in targetType.Methods) {
				if (!UTF8String.Equals(method.Name, mr.Name))
					continue;
				if (comparer.Equals(method.MethodSig, mr.MethodSig))
					return method;
			}
			return null;
		}

		IField Import(IField field) {
			if (field == null)
				return null;

			var fd = field as FieldDef;
			if (fd != null)
				return oldFieldToNewField[fd];

			var mr = (MemberRef)field;
			ImportedType importedType;
			var td = TryGetTypeInTargetModule(mr.Class as ITypeDefOrRef, out importedType);
			if (td != null) {
				var targetField = FindField(td, mr);
				if (targetField != null)
					return targetField;
				if (importedType != null) {
					var compiledField = FindField(toExtraData[importedType].CompiledType, mr);
					if (compiledField != null)
						return oldFieldToNewField[compiledField];
				}

				AddError(IM0005, string.Format(dnSpy_AsmEditor_Resources.ERR_IM_CouldNotFindField, mr));
				return null;
			}

			return ImportNoCheckForDefs(mr);
		}

		FieldDef FindField(TypeDef targetType, MemberRef mr) {
			var comparer = new ImportSigComparer(importSigComparerOptions, SIG_COMPARER_OPTIONS, targetModule);
			foreach (var field in targetType.Fields) {
				if (!UTF8String.Equals(field.Name, mr.Name))
					continue;
				if (comparer.Equals(field.FieldSig, mr.FieldSig))
					return field;
			}
			return null;
		}

		MemberRef ImportNoCheckForDefs(MemberRef mr) {
			var importedMemberRef = targetModule.UpdateRowId(new MemberRefUser(targetModule, mr.Name));
			ImportCustomAttributes(importedMemberRef, mr);
			importedMemberRef.Signature = Import(mr.Signature);
			importedMemberRef.Class = Import(mr.Class);
			return importedMemberRef;
		}

		IMemberRefParent Import(IMemberRefParent cls) {
			if (cls == null)
				return null;

			var tdr = cls as ITypeDefOrRef;
			if (tdr != null)
				return Import(tdr);

			var md = cls as MethodDef;
			if (md != null)
				return oldMethodToNewMethod[md];

			var modRef = cls as ModuleRef;
			if (modRef != null)
				return Import(modRef, true);

			throw new InvalidOperationException();
		}
	}
}
