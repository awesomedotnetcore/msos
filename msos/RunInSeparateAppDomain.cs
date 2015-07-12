﻿using Microsoft.CSharp;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.RuntimeExt;
using System;
using System.CodeDom.Compiler;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Remoting;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo(msos.RunInSeparateAppDomain.CompiledQueryAssemblyName)]

namespace msos
{
    internal class RunQueryContext
    {
        public ClrHeap Heap { get; set; }

        public IEnumerable<dynamic> ObjectsOfType(string typeName)
        {
            return from obj in Heap.EnumerateObjects()
                   let type = Heap.GetObjectType(obj)
                   where type != null && typeName == type.Name
                   select Heap.GetDynamicObject(obj);
        }

        public IEnumerable<dynamic> AllObjects()
        {
            return from obj in Heap.EnumerateObjects()
                   select Heap.GetDynamicObject(obj);
        }

        public dynamic Class(string typeName)
        {
            return Heap.GetDynamicClass(typeName);
        }

        public IEnumerable<dynamic> AllClasses()
        {
            return from type in Heap.EnumerateTypes()
                   select Heap.GetDynamicClass(type.Name);
        }

        public dynamic Object(ulong address)
        {
            return Heap.GetDynamicObject(address);
        }
    }

    internal interface IRunQuery
    {
        object Run();
    }

    [Serializable]
    public class RunFailedException : Exception
    {
        public RunFailedException(string message)
            : base(message)
        {
        }

        protected RunFailedException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }

    class RunInSeparateAppDomain : MarshalByRefObject, IDisposable
    {
        internal const string CompiledQueryAssemblyName = "msos_CompiledQuery";
        const int AttachTimeout = 1000;
        const int TotalWidth = 100;
        const string CompiledQueryPlaceholder = "$$$QUERY$$$";
        const string CompiledQueryTemplate = @"
using Microsoft.CSharp;
using Microsoft.Diagnostics.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using msos;

[assembly: InternalsVisibleTo(""msos"")]

internal class RunQuery : IRunQuery
{
    private RunQueryContext _context;

    public RunQuery(RunQueryContext context)
    {
        _context = context;
    }

    private IEnumerable<dynamic> AllObjects()
    {
        return _context.AllObjects();
    }

    private IEnumerable<dynamic> ObjectsOfType(string typeName)
    {
        return _context.ObjectsOfType(typeName);
    }

    private dynamic Class(string typeName)
    {
        return _context.Class(typeName);
    }

    private IEnumerable<dynamic> AllClasses()
    {
        return _context.AllClasses();
    }

    private dynamic Object(ulong address)
    {
        return _context.Object(address);
    }

    public object Run()
    {
        return (" + CompiledQueryPlaceholder + @");
    }
}
";

        private TextWriter _writer;
        private ClrHeap _heap;
        private DataTarget _target;

        private void CreateRuntime(string dacLocation)
        {
            ClrRuntime runtime = _target.CreateRuntime(dacLocation);
            _heap = runtime.GetHeap();
        }

        public RunInSeparateAppDomain(string dumpFile, string dacLocation, TextWriter writer)
        {
            _target = DataTarget.LoadCrashDump(dumpFile, CrashDumpReader.ClrMD);
            CreateRuntime(dacLocation);
            _writer = writer;
        }

        public RunInSeparateAppDomain(int pid, string dacLocation, TextWriter writer)
        {
            _target = DataTarget.AttachToProcess(pid, AttachTimeout, AttachFlag.Passive);
            CreateRuntime(dacLocation);
            _writer = writer;
        }

        public void RunQuery(string outputFormat, string query)
        {
            var options = new CompilerParameters();
            options.ReferencedAssemblies.Add(typeof(Enumerable).Assembly.Location);
            options.ReferencedAssemblies.Add(Assembly.GetExecutingAssembly().Location);
            options.ReferencedAssemblies.Add(typeof(ClrHeap).Assembly.Location);
            options.ReferencedAssemblies.Add(typeof(Microsoft.CSharp.RuntimeBinder.RuntimeBinderException).Assembly.Location);
            options.CompilerOptions = "/optimize+";
            options.OutputAssembly = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                CompiledQueryAssemblyName) + ".dll";

            string source = CompiledQueryTemplate.Replace(CompiledQueryPlaceholder, query);

            var compiler = new CSharpCodeProvider();
            CompilerResults results = compiler.CompileAssemblyFromSource(options, source);

            if (results.Errors.HasErrors)
            {
                throw new RunFailedException(
                    String.Format("Query compilation failed with {0} errors:" + Environment.NewLine + "{1}",
                    results.Errors.Count,
                    String.Join(Environment.NewLine, (from error in results.Errors.Cast<CompilerError>() select error.ToString()).ToArray())
                    ));
            }

            Type compiledQueryType = results.CompiledAssembly.GetType("RunQuery");
            IRunQuery runQuery = (IRunQuery)Activator.CreateInstance(
                compiledQueryType, new RunQueryContext { Heap = _heap });
            
            object result = runQuery.Run();

            IObjectPrinter printer = null;
            switch (outputFormat)
            {
                case HeapQuery.TabularOutputFormat:
                    printer = new TabularObjectPrinter(_writer);
                    break;
                case HeapQuery.JsonOutputFormat:
                    printer = new JsonObjectPrinter(_writer);
                    break;
                default:
                    throw new NotSupportedException(String.Format(
                        "The output format '{0}' is not supported", outputFormat));
            }

            IEnumerable enumerable = result as IEnumerable;
            ulong rowCount = 0;
            if (enumerable != null && !(result is string))
            {
                bool first = true;
                foreach (var obj in enumerable)
                {
                    if (obj is ulong)
                    {
                        _writer.WriteLine(((ulong)obj).ToString("x16"));
                    }
                    else if (obj.IsAnonymousType())
                    {
                        if (first)
                        {
                            printer.PrintHeader(obj);
                            first = false;
                        }
                        printer.PrintContents(obj);
                    }
                    else
                    {
                        _writer.WriteLine(obj.ToString());
                    }
                    ++rowCount;
                }
            }
            else
            {
                _writer.WriteLine(result.ToString());
                ++rowCount;
            }
            _writer.WriteLine("Rows: {0}", rowCount);
        }

        interface IObjectPrinter
        {
            void PrintHeader(object obj);
            void PrintContents(object obj);
        }

        abstract class ObjectPrinterBase : IObjectPrinter
        {
            protected TextWriter Output { get; private set; }

            protected ObjectPrinterBase(TextWriter output)
            {
                Output = output;
            }

            public abstract void PrintHeader(object obj);
            public abstract void PrintContents(object obj);
        }

        class TabularObjectPrinter : ObjectPrinterBase
        {
            public TabularObjectPrinter(TextWriter output)
                : base(output)
            {
            }

            public override void PrintHeader(object obj)
            {
                var props = obj.GetType().GetProperties();
                int width = TotalWidth / props.Length;
                for (int i = 0; i < props.Length; ++i)
                {
                    // Do not restrict the width of the last property.
                    if (i == props.Length - 1)
                    {
                        Output.Write(props[i].Name.TrimEndToLength(width));
                    }
                    else
                    {
                        Output.Write("{0,-" + width + "}  ", props[i].Name.TrimEndToLength(width));
                    }
                }
                Output.WriteLine();
            }

            public override void PrintContents(object obj)
            {
                var props = obj.GetType().GetProperties();
                int width = TotalWidth / props.Length;
                for (int i = 0; i < props.Length; ++i)
                {
                    // Do not restrict the width of the last property.
                    if (i == props.Length - 1)
                    {
                        Output.Write(props[i].GetValue(obj).ToString());
                    }
                    else
                    {
                        Output.Write("{0,-" + width + "}  ", props[i].GetValue(obj).ToString().TrimEndToLength(width));
                    }
                }
                Output.WriteLine();
            }
        }

        class JsonObjectPrinter : ObjectPrinterBase
        {
            public JsonObjectPrinter(TextWriter output)
                : base(output)
            {
            }

            public override void PrintHeader(object obj)
            {
            }

            public override void PrintContents(object obj)
            {
                Output.WriteLine("{");
                var props = obj.GetType().GetProperties();
                foreach (var prop in props)
                {
                    Output.WriteLine("  {0} = {1}", prop.Name, prop.GetValue(obj).ToString());
                }
                Output.WriteLine("}");
            }
        }

        public void Dispose()
        {
            _target.Dispose();
            RemotingServices.Disconnect(this);
        }

        public override object InitializeLifetimeService()
        {
            return null;
        }
    }
}
