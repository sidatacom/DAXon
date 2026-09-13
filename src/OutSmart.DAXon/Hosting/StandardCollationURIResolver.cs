////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
// Copyright (c) 2018-2023 Saxonica Limited
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.
////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
using OutSmart.DAXon.Core;
using OutSmart.DAXon.Transformation;
using OutSmart.DAXon.Values;
using OutSmart.DAXon.Internal.Net;
using OutSmart.DAXon.Internal.Collections;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using OutSmart.DAXon.Functions;
using OutSmart.DAXon.Internal;
namespace OutSmart.DAXon.Lib
{
    internal class StandardCollationURIResolver : ICollationURIResolver
    {
        private static readonly StandardCollationURIResolver theInstance = new StandardCollationURIResolver();
        public StandardCollationURIResolver()
        {
        }

        public static StandardCollationURIResolver GetInstance()
        {
            return theInstance;
        }

        private static URI ParseCollationUri(string uri)
        {
            try
            {
                return new URI(uri);
            }
            catch (URISyntaxException err)
            {
                throw new XPathException(err?.Message);
            }
        }

        // kw=val pairs of a collation-URI query: strips a leading '?' if present, splits on
        // ';'/'&', skips malformed params. A null query (URI with a bare trailing '?': RawQuery
        // is null then) yields nothing — Java's getRawQuery() returns "" there, and the empty
        // props must produce the default collation, not a crash.
        private static IEnumerable<KeyValuePair<string, string>> QueryParams(string query)
        {
            if (query == null)
            {
                yield break;
            }

            if (query.StartsWith("?", StringComparison.Ordinal))
            {
                query = query.Substring(1);
            }

            foreach (string param in query.Split(new[] { ';', '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = param.IndexOf('=');
                if (eq > 0 && eq < param.Length - 1)
                {
                    yield return new KeyValuePair<string, string>(param.Substring(0, eq), param.Substring(eq + 1));
                }
            }
        }

        public virtual IStringCollator Resolve(string uri, Configuration config)
        {
            if (uri.Equals("http://saxon.sf.net/collation"))
            {
                return Core.Version.platform.MakeCollation(config, new Properties(), uri);
            }
            else if (uri.StartsWith("http://saxon.sf.net/collation?", StringComparison.Ordinal))
            {
                URI uuri = ParseCollationUri(uri);
                Properties props = new Properties();
                foreach (KeyValuePair<string, string> p in QueryParams(uuri.RawQuery))
                {
                    props.SetProperty(p.Key, AnyURIValue.Decode(p.Value));
                }

                return Core.Version.platform.MakeCollation(config, props, uri);
            }
            else if (uri.StartsWith("http://www.w3.org/2013/collation/UCA", StringComparison.Ordinal))
            {
                IStringCollator uca = Core.Version.platform.MakeUcaCollator(uri, config);
                if (uca != null)
                {
                    return uca;
                }

                throw new XPathException("The UCA collation is not supported because this platform has no exact Unicode Collation Algorithm implementation", "FOCH0002");
            }
            else
            {
                return null;
            }
        }
    }
}