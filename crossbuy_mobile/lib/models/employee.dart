class Employee {
  final int id;
  final String? firstName;
  final String? lastName;
  final String? fullName;
  final String? fullNameEn;
  final String? email;
  final String? phoneNumber;
  final String? address;
  final String? gender;
  final String? maritalStatus;
  final String? profileImage;
  final DateTime? dateOfBirth;
  final DateTime? dateOfJoining;
  final String? jobTitleAr;
  final String? jobTitleEn;
  final String? companyAr;
  final String? companyEn;
  final String? branchAr;
  final String? branchEn;
  final bool isActive;
  final String? employmentType;
  final String? departmentAr;
  final String? departmentEn;

  Employee({
    required this.id,
    this.firstName,
    this.lastName,
    this.fullName,
    this.fullNameEn,
    this.email,
    this.phoneNumber,
    this.address,
    this.gender,
    this.maritalStatus,
    this.profileImage,
    this.dateOfBirth,
    this.dateOfJoining,
    this.jobTitleAr,
    this.jobTitleEn,
    this.companyAr,
    this.companyEn,
    this.branchAr,
    this.branchEn,
    this.isActive = true,
    this.employmentType,
    this.departmentAr,
    this.departmentEn,
  });

  static DateTime? _date(dynamic v) =>
      v == null ? null : DateTime.tryParse(v.toString());

  factory Employee.fromJson(Map<String, dynamic> j) => Employee(
        id: j['id'] ?? j['ID'] ?? 0,
        firstName: j['firstName'],
        lastName: j['lastName'],
        fullName: j['fullName'],
        fullNameEn: j['fullNameEn'],
        email: j['email'],
        phoneNumber: j['phoneNumber'],
        address: j['address'],
        gender: j['gender'],
        maritalStatus: j['maritalStatus'],
        profileImage: j['profileImage'],
        dateOfBirth: _date(j['dateOfBirth']),
        dateOfJoining: _date(j['dateOfJoining']),
        jobTitleAr: j['jobTitleAr'],
        jobTitleEn: j['jobTitleEn'],
        companyAr: j['companyAr'],
        companyEn: j['companyEn'],
        branchAr: j['branchAr'],
        branchEn: j['branchEn'],
        isActive: j['isActive'] ?? true,
        employmentType: j['employmentType'],
        departmentAr: j['departmentAr'],
        departmentEn: j['departmentEn'],
      );
}
