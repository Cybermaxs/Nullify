﻿using Nullify.Configuration;
using Nullify.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Nullify.Factory
{
    internal class TypeFactory
    {
        private readonly ICreationScope scope;
        private readonly ITypeRegistry typeRegistry;

        public TypeFactory(ICreationScope scope, ITypeRegistry typeRegistry)
        {
            this.typeRegistry = typeRegistry;
            this.scope = scope;
        }

        public Type Create(CreationPolicy policy)
        {
            //have to create a new one
            var typeBuilder = typeRegistry.CreateTypeBuilder(policy.Target, policy.AutoGeneratedClassName);

            // all interfaces to implement
            var types = new List<Type>();
            types.Add(policy.Target);
            types.AddRange(policy.Target.GetInterfaces());

            //fill target type with all members
            foreach (var baseType in types)
            {
                FillMethods(typeBuilder, baseType, policy);
                FillEvents(typeBuilder, baseType, policy);
                FillProperties(typeBuilder, baseType, policy);
            }

            //create type
            try
            {
                var newType = typeBuilder.CreateType();
                return newType;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        private void FillProperties(TypeBuilder typeBuilder, Type baseType, CreationPolicy policy)
        {
            var properties = baseType.GetProperties();

            foreach (var property in properties)
            {
                var indexParameters = property.GetIndexParameters().Select(p => p.ParameterType).ToArray();
                var propertyBuilder = typeBuilder.DefineProperty(property.Name, property.Attributes, property.PropertyType, indexParameters);

                if (property.CanRead)
                {
                    // Generate getter method
                    var getter = typeBuilder.DefineMethod("get_" + property.Name, MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.NewSlot, property.PropertyType, indexParameters);
                    var il = getter.GetILGenerator();
                    il.Emit(OpCodes.Nop);
                    il.DeclareLocal(property.PropertyType);
                    EmitResult(il, property, property.PropertyType, policy);
                    il.Emit(OpCodes.Stloc_0);
                    il.Emit(OpCodes.Ldloc_0);
                    il.Emit(OpCodes.Ret);
                    propertyBuilder.SetGetMethod(getter);
                }

                if (property.CanWrite)
                {
                    var types = new List<Type>();
                    if (indexParameters.Length > 0)
                        types.AddRange(indexParameters);
                    types.Add(property.PropertyType);
                    var setter = typeBuilder.DefineMethod("set_" + property.Name, MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.NewSlot, null, types.ToArray());
                    var il = setter.GetILGenerator();

                    il.Emit(OpCodes.Nop);        // Push "this" on the stack
                    il.Emit(OpCodes.Ret);        // Push "value" on the stack

                    propertyBuilder.SetSetMethod(setter);
                }
            }
        }

        private void FillEvents(TypeBuilder typeBuilder, Type baseType, CreationPolicy policy)
        {
            var events = baseType.GetEvents();

            foreach (var evt in events)
            {
                // Event methods attributes
                var eventMethodAttr = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Final | MethodAttributes.SpecialName;
                var eventMethodImpAtr = MethodImplAttributes.Managed;

                var qualifiedEventName = evt.Name;
                var addMethodName = string.Format("add_{0}", evt.Name);
                var remMethodName = string.Format("remove_{0}", evt.Name);

                //FieldBuilder eFieldBuilder = typeBuilder.DefineField(qualifiedEventName, evt.EventHandlerType, FieldAttributes.Public);

                var eBuilder = typeBuilder.DefineEvent(qualifiedEventName, EventAttributes.None, evt.EventHandlerType);

                // ADD method
                var addMethodBuilder = typeBuilder.DefineMethod(addMethodName, eventMethodAttr, null, new Type[] { evt.EventHandlerType });

                addMethodBuilder.SetImplementationFlags(eventMethodImpAtr);

                // Code generation
                var ilgen = addMethodBuilder.GetILGenerator();
                ilgen.Emit(OpCodes.Ret);

                // REMOVE method
                var removeMethodBuilder = typeBuilder.DefineMethod(remMethodName, eventMethodAttr, null, new Type[] { evt.EventHandlerType });
                removeMethodBuilder.SetImplementationFlags(eventMethodImpAtr);

                var removeInfo = typeof(Delegate).GetMethod("Remove", new Type[] { typeof(Delegate), typeof(Delegate) });

                // Code generation
                ilgen = removeMethodBuilder.GetILGenerator();
                ilgen.Emit(OpCodes.Ret);

                // Finally, setting the AddOn and RemoveOn methods for our event
                eBuilder.SetAddOnMethod(addMethodBuilder);
                eBuilder.SetRemoveOnMethod(removeMethodBuilder);

                // Implement the method from the interface
                typeBuilder.DefineMethodOverride(addMethodBuilder, evt.GetAddMethod(false));

                // Implement the method from the interface
                typeBuilder.DefineMethodOverride(removeMethodBuilder, evt.GetRemoveMethod(false));
            }
        }

        private void FillMethods(TypeBuilder typeBuilder, Type baseType, CreationPolicy policy)
        {
            var methods = baseType.GetMethods();

            foreach (var method in methods)
            {
                if (method.IsSpecialName)
                    continue;//properties/events,indexers

                var methodBuilder = typeBuilder.DefineMethod(
                    method.Name,
                    MethodAttributes.Public | MethodAttributes.Final | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                             method.ReturnType ?? typeof(void), method.GetParameters().Select(p => p.ParameterType).ToArray()
                             );

                typeBuilder.DefineMethodOverride(methodBuilder, method);

                var il = methodBuilder.GetILGenerator();
                il.Emit(OpCodes.Nop);

                if (method.ReturnType != typeof(void))
                {
                    il.DeclareLocal(method.ReturnType);
                    EmitResult(il, method, method.ReturnType, policy);
                    il.Emit(OpCodes.Stloc_0);
                    il.Emit(OpCodes.Ldloc_0);
                }

                il.Emit(OpCodes.Ret);
            }
        }

        private void EmitResult(ILGenerator il, MemberInfo memberInfo, Type returnType, CreationPolicy policy)
        {
            object ret;
            if (policy.ReturnValues.TryGetValue(memberInfo, out ret))
            {
                //no configured value
                if (ret != null)
                    il.EmitConstant(ret);
                else
                    il.EmitDefault(returnType);
            }
            else if (returnType.IsInterface)
            {
                Type nulloft;
                // try get nullified type
                if (scope.TryGet(returnType, policy.Name, out nulloft))
                {
                    il.EmitNew(nulloft.GetConstructor(Type.EmptyTypes));
                }
                else
                    il.EmitDefault(returnType);
            }
            else if (!returnType.IsInterface)
            {
                var ctor = returnType.GetConstructor(Type.EmptyTypes);
                // try get nullified type
                if (ctor != null)
                {
                    il.EmitNew(ctor);
                }
                else
                    il.EmitDefault(returnType);
            }
            else
            {
                //emit default
                il.EmitDefault(returnType);
            }
        }

        public bool CanCreate()
        {
            return true;
        }
    }
}
