﻿using System;
using Couchbase.Configuration;
using Couchbase.Configuration.Client;
using NUnit.Framework;
using System.Collections.Generic;

namespace Couchbase.Tests.Authentication
{
    [TestFixture]
    public class BucketAuthenticationTests
    {
        [Test]
        public void When_Valid_Credentials_Provided_Bucket_Created_Succesfully()
        {
            CouchbaseCluster.Initialize(new ClientConfiguration
            {
                BucketConfigs = new Dictionary<string, BucketConfiguration>
                {
                    {
                        "authenticated",
                        new BucketConfiguration
                        {
                            BucketName = "authenticated"
                        }
                    }
                }
            });

            var cluster = CouchbaseCluster.Get();
            var bucket = cluster.OpenBucket("authenticated", "secret");
            cluster.CloseBucket(bucket);
            Assert.IsNotNull(bucket);
            cluster.CloseBucket(bucket);
        }

        [Test]
        [ExpectedException(typeof(ConfigException))]
        public void When_InValid_Credentials_Provided_Bucket_Created_UnSuccesfully()
        {
            CouchbaseCluster.Initialize(new ClientConfiguration
            {
                Servers = new List<Uri> { new Uri("http://192.168.56.102:8091") },
                BucketConfigs = new Dictionary<string, BucketConfiguration>
                {
                    {
                        "authenticated",
                        new BucketConfiguration
                        {
                            BucketName = "authenticated"
                        }
                    }
                }
            });
            var cluster = CouchbaseCluster.Get();
            var bucket = cluster.OpenBucket("authenticated", "secretw"); 
            cluster.CloseBucket(bucket);
            Assert.IsNotNull(bucket);
        }
    }
}

#region [ License information          ]

/* ************************************************************
 *
 *    @author Couchbase <info@couchbase.com>
 *    @copyright 2014 Couchbase, Inc.
 *
 *    Licensed under the Apache License, Version 2.0 (the "License");
 *    you may not use this file except in compliance with the License.
 *    You may obtain a copy of the License at
 *
 *        http://www.apache.org/licenses/LICENSE-2.0
 *
 *    Unless required by applicable law or agreed to in writing, software
 *    distributed under the License is distributed on an "AS IS" BASIS,
 *    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *    See the License for the specific language governing permissions and
 *    limitations under the License.
 *
 * ************************************************************/

#endregion