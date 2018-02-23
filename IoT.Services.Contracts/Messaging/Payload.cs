﻿using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace IoT.Services.Contracts.Messaging
{
    public class Payload
    {
        [JsonProperty]
        public PayloadType PayloadType { get; set; }
        [JsonProperty]
        public String PayloadText { get; set; }

        [JsonConstructor]
        public Payload()
        {
        }
    }
}