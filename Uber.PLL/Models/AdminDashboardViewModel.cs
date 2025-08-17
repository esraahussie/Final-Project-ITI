using Uber.DAL.Entities;

namespace Uber.PLL.Models
{
    public class AdminDashboardViewModel
    {
        public List<User> Riders { get; set; }
        public List<Driver> Drivers { get; set; }
    }
}
