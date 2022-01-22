using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace Granfeldt
{
    [Serializable]
    public class WithinHoursSpan : ConditionBase
    {
        [XmlAttribute("From")]
        public int From { get; set; }
        [XmlAttribute("To")]
        public int To { get; set; }

        public override string Name => $"Condition {nameof(WithinHoursSpan)} (from: {this.From}, to: {this.To})";
        public override bool IsMet(string ThreadName = null)
        {
            DateTime d = DateTime.Now;
            return d.Hour >= this.From && d.Hour <= To;
        }

    }
}
