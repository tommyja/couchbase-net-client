using System;
using System.Collections.Generic;
using System.Text;

namespace Couchbase.Core.Diagnostics.Tracing
{
    /// <summary>
    /// A span used internally by CouchbaseNetClient for tracing.
    /// </summary>
    /// <remarks>Volatile.  (This interface may change in breaking ways during minor releases)</remarks>
    public interface IInternalSpan : IRequestSpan
    {
        IInternalSpan StartPayloadEncoding();

        IInternalSpan StartDispatch();

        IInternalSpan SetAttribute(string key, string value);
    }
}
