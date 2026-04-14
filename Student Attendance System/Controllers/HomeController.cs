using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Http;
using System.Collections.Generic;

namespace CGatePortal.Controllers
{
    public class HomeController : Controller
    {
        private readonly string _conn;

        // The Constructor: This connects the App to the SQL Database via the Connection String
        public HomeController(IConfiguration configuration)
        {
            _conn = configuration.GetConnectionString("DefaultConnection") ?? "";
        }

        // --- DASHBOARD: MAIN MONITORING PAGE ---
        public IActionResult Index()
        {
            // SECURITY: Check if a teacher is logged in. If not, kick them back to Login.
            string? subject = HttpContext.Session.GetString("TeacherSubject");
            if (string.IsNullOrEmpty(subject)) return RedirectToAction("Login");

            ViewBag.CurrentSubject = subject;
            ViewBag.CurrentSection = "SECTION 10";

            var displayList = new List<dynamic>();
            using (SqlConnection s = new SqlConnection(_conn))
            {
                s.Open();

                // SQL LOGIC: Uses a LEFT JOIN so students show up even if they haven't checked in yet.
                // It calculates 'LATE' or 'ABSENT' in real-time based on the SQL Server clock.
                string sql = @"
            SELECT 
                M.FullName, 
                CASE 
                    WHEN A.Status IS NOT NULL THEN A.Status
                    WHEN CAST(GETDATE() AS TIME) > '16:30:00' THEN 'ABSENT'
                    WHEN CAST(GETDATE() AS TIME) BETWEEN '15:16:00' AND '16:30:00' THEN 'LATE'
                    WHEN CAST(GETDATE() AS TIME) BETWEEN '15:00:00' AND '15:15:59' THEN 'PENDING'
                    ELSE 'WAITING' 
                END AS Status, 
                A.TimeIn 
            FROM StudentMasterList M
            LEFT JOIN AttendanceLog A ON M.FullName = A.FullName 
                AND M.SubjectName = A.SubjectName 
                AND CAST(A.TimeIn AS DATE) = CAST(GETDATE() AS DATE)
            WHERE M.SubjectName = @sub
            ORDER BY 
                CASE 
                    WHEN A.Status = 'PRESENT' THEN 1
                    WHEN A.Status = 'LATE' THEN 2
                    WHEN CAST(GETDATE() AS TIME) > '16:30:00' AND A.Status IS NULL THEN 4
                    ELSE 3 
                END, 
                M.FullName ASC";

                SqlCommand cmd = new SqlCommand(sql, s);
                cmd.Parameters.AddWithValue("@sub", subject);

                using (SqlDataReader r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        displayList.Add(new
                        {
                            FullName = r["FullName"].ToString(),
                            Status = r["Status"].ToString(),
                            TimeIn = r["TimeIn"] != DBNull.Value ? (DateTime?)Convert.ToDateTime(r["TimeIn"]) : null
                        });
                    }
                }
            }
            return View(displayList);
        }

        // --- ATTENDANCE LOGIC: PROCESSING STUDENT CHECK-IN ---
        [HttpPost]
        public IActionResult CheckIn(string studentFullName)
        {
            string? subject = HttpContext.Session.GetString("TeacherSubject");
            if (string.IsNullOrEmpty(subject)) return RedirectToAction("Login");

            string cleanName = studentFullName.Trim().ToUpper();
            DateTime now = DateTime.Now;
            TimeSpan currentTime = now.TimeOfDay;

            // CONFIGURATION: Define the school's schedule windows
            TimeSpan startOfClass = new TimeSpan(15, 0, 0);    // 3:00 PM
            TimeSpan lateThreshold = new TimeSpan(15, 15, 59); // 3:15 PM
            TimeSpan absentThreshold = new TimeSpan(16, 30, 0); // 4:30 PM

            // VALIDATION 1: Prevent early check-ins
            if (currentTime < startOfClass)
            {
                TempData["Error"] = "TOO EARLY. PLEASE WAIT FOR YOUR CLASS SCHEDULE AT 3:00 PM.";
                return RedirectToAction("Index");
            }

            // LOGIC: Automatically determine status based on current system time
            string status = (currentTime <= lateThreshold) ? "PRESENT" :
                            (currentTime <= absentThreshold) ? "LATE" : "ABSENT";

            if (status == "ABSENT")
            {
                TempData["Error"] = "YOU HAVE BEEN MARKED ABSENT. THE CHECK-IN PERIOD HAS ENDED.";
            }

            try
            {
                using (SqlConnection s = new SqlConnection(_conn))
                {
                    s.Open();

                    // VALIDATION 2: Verify student exists in the Master List for this subject
                    string vSql = "SELECT COUNT(*) FROM StudentMasterList WHERE FullName=@n AND SubjectName=@sub";
                    SqlCommand vCmd = new SqlCommand(vSql, s);
                    vCmd.Parameters.AddWithValue("@n", cleanName);
                    vCmd.Parameters.AddWithValue("@sub", subject);

                    if ((int)vCmd.ExecuteScalar() == 0)
                    {
                        TempData["Error"] = "STUDENT NOT FOUND IN CLASS MASTER LIST.";
                        return RedirectToAction("Index");
                    }

                    // VALIDATION 3: Prevent double check-ins for the same day
                    string dSql = "SELECT COUNT(*) FROM AttendanceLog WHERE FullName=@n AND SubjectName=@sub AND CAST(TimeIn AS DATE) = CAST(GETDATE() AS DATE)";
                    SqlCommand dCmd = new SqlCommand(dSql, s);
                    dCmd.Parameters.AddWithValue("@n", cleanName);
                    dCmd.Parameters.AddWithValue("@sub", subject);

                    if ((int)dCmd.ExecuteScalar() > 0)
                    {
                        TempData["Error"] = "YOU HAVE ALREADY CHECKED IN FOR TODAY.";
                        return RedirectToAction("Index");
                    }

                    // DATABASE ACTION: Save the attendance record
                    string logSql = "INSERT INTO AttendanceLog (StudentId, FullName, Section, SubjectName, Status, TimeIn) VALUES (@id, @n, 'SECTION 10', @sub, @stat, @dt)";
                    SqlCommand logCmd = new SqlCommand(logSql, s);
                    logCmd.Parameters.AddWithValue("@id", "STU-" + Guid.NewGuid().ToString().Substring(0, 8)); // Generates unique ID
                    logCmd.Parameters.AddWithValue("@n", cleanName);
                    logCmd.Parameters.AddWithValue("@sub", subject);
                    logCmd.Parameters.AddWithValue("@stat", status);
                    logCmd.Parameters.AddWithValue("@dt", now);
                    logCmd.ExecuteNonQuery();

                    if (status != "ABSENT") TempData["Success"] = $"{cleanName} MARKED {status}";
                }
            }
            catch { TempData["Error"] = "DATABASE CONNECTION ERROR."; }
            return RedirectToAction("Index");
        }

        // --- MANAGEMENT: ADDING STUDENTS & RESETTING DATA ---
        [HttpPost]
        public IActionResult AddToMasterList(string fullName, string confirmPassword)
        {
            string? subject = HttpContext.Session.GetString("TeacherSubject");
            using (SqlConnection s = new SqlConnection(_conn))
            {
                s.Open();
                // AUTHENTICATION: Verify instructor password before adding to database
                string auth = "SELECT COUNT(*) FROM Users WHERE AssignedSubject=@sub AND Password=@p";
                SqlCommand aCmd = new SqlCommand(auth, s);
                aCmd.Parameters.AddWithValue("@sub", subject);
                aCmd.Parameters.AddWithValue("@p", confirmPassword);

                if ((int)aCmd.ExecuteScalar() > 0)
                {
                    string add = "INSERT INTO StudentMasterList (FullName, SubjectName, Section) VALUES (@n, @sub, 'SECTION 10')";
                    SqlCommand cmd = new SqlCommand(add, s);
                    cmd.Parameters.AddWithValue("@n", fullName.Trim().ToUpper());
                    cmd.Parameters.AddWithValue("@sub", subject);
                    cmd.ExecuteNonQuery();
                    TempData["Success"] = "STUDENT ADDED TO CLASS MASTER LIST.";
                }
                else { TempData["Error"] = "INVALID ADMIN PASSWORD."; }
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public IActionResult ResetAttendance(string confirmPassword)
        {
            string? subject = HttpContext.Session.GetString("TeacherSubject");
            using (SqlConnection s = new SqlConnection(_conn))
            {
                s.Open();
                string auth = "SELECT COUNT(*) FROM Users WHERE AssignedSubject=@sub AND Password=@p";
                SqlCommand aCmd = new SqlCommand(auth, s);
                aCmd.Parameters.AddWithValue("@sub", subject);
                aCmd.Parameters.AddWithValue("@p", confirmPassword);

                if ((int)aCmd.ExecuteScalar() > 0)
                {
                    // DATABASE ACTION: Deletes logs ONLY for today and ONLY for this subject
                    string del = "DELETE FROM AttendanceLog WHERE SubjectName=@sub AND CAST(TimeIn AS DATE) = CAST(GETDATE() AS DATE)";
                    SqlCommand delCmd = new SqlCommand(del, s);
                    delCmd.Parameters.AddWithValue("@sub", subject);
                    delCmd.ExecuteNonQuery();
                    TempData["Success"] = "TODAY'S LOGS CLEARED.";
                }
                else { TempData["Error"] = "RESET FAILED: WRONG PASSWORD."; }
            }
            return RedirectToAction("Index");
        }

        // --- AUTHENTICATION MODULE ---
        public IActionResult Login() => View();

        [HttpPost]
        public IActionResult Login(string username, string password)
        {
            using (SqlConnection s = new SqlConnection(_conn))
            {
                s.Open();
                // Matches Username and Password to find which Subject the teacher handles
                string sql = "SELECT AssignedSubject FROM Users WHERE Username=@u AND Password=@p";
                SqlCommand cmd = new SqlCommand(sql, s);
                cmd.Parameters.AddWithValue("@u", username);
                cmd.Parameters.AddWithValue("@p", password);
                var result = cmd.ExecuteScalar();
                if (result != null)
                {
                    // SESSION: Stores the subject name so the teacher only sees their own students
                    HttpContext.Session.SetString("TeacherSubject", result.ToString() ?? "");
                    return RedirectToAction("Index");
                }
            }
            ViewBag.Error = "INVALID CREDENTIALS";
            return View();
        }

        public IActionResult Register() => View();

        [HttpPost]
        public IActionResult Register(string username, string password, string subject)
        {
            // SECURITY BYPASS: Feature manually disabled for defense demo safety
            TempData["Error"] = "THIS FEATURE IS NOT AVAILABLE AT THIS MOMENT.";
            return RedirectToAction("Register");
        }

        public IActionResult Logout()
        {
            HttpContext.Session.Clear(); // Ends the session and logs the teacher out
            return RedirectToAction("Login");
        }
    }
}