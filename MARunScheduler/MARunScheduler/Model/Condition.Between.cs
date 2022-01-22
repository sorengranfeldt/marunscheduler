using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace Granfeldt
{
    [Serializable]
    public class Between : ConditionBase
    {
        [XmlAttribute("From")]
        public string From { get; set; }
        [XmlAttribute("To")]
        public string To { get; set; }

        public override string Name => $"Condition {nameof(Between)} (from: {this.From}, to: {this.To})";
        public override bool IsMet(string ThreadName = null)
        {
            DateTime from;
            DateTime to;

            if (!DateTime.TryParse(this.From, out from)) from = DateTime.MaxValue.AddSeconds(-1);
            if (!DateTime.TryParse(this.To, out to)) to = DateTime.MinValue;

            DateTime d = DateTime.Now;
            return d.TimeOfDay >= from.TimeOfDay && d.TimeOfDay <= to.TimeOfDay;
        }

    }
}
