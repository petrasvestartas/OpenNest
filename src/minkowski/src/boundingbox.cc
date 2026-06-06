#include "boundingbox.h"

typedef std::vector<Eigen::Vector3d>::iterator VectorIter;
typedef std::vector<Eigen::Vector3d>::const_iterator VectorCIter;
typedef std::vector<Vertex>::iterator VertexIter;
typedef std::vector<Vertex>::const_iterator VertexCIter;

BoundingBox::BoundingBox():
min(Eigen::Vector3d::Zero()),
max(Eigen::Vector3d::Zero()),
extent(Eigen::Vector3d::Zero())
{
    
}

BoundingBox::BoundingBox(const Eigen::Vector3d& min0, const Eigen::Vector3d& max0):
min(min0),
max(max0)
{
    extent = max - min;
}

BoundingBox::BoundingBox(const Eigen::Vector3d& p):
min(p),
max(p)
{
    extent = max - min;
}

void BoundingBox::expandToInclude(const Eigen::Vector3d& p)
{
    if (min.x() > p.x()) min.x() = p.x();
    if (min.y() > p.y()) min.y() = p.y();
    if (min.z() > p.z()) min.z() = p.z();
    
    if (max.x() < p.x()) max.x() = p.x();
    if (max.y() < p.y()) max.y() = p.y();
    if (max.z() < p.z()) max.z() = p.z();
    
    extent = max - min;
}

void BoundingBox::expandToInclude(const BoundingBox& b)
{
    if (min.x() > b.min.x()) min.x() = b.min.x();
    if (min.y() > b.min.y()) min.y() = b.min.y();
    if (min.z() > b.min.z()) min.z() = b.min.z();
    
    if (max.x() < b.max.x()) max.x() = b.max.x();
    if (max.y() < b.max.y()) max.y() = b.max.y();
    if (max.z() < b.max.z()) max.z() = b.max.z();
    
    extent = max - min;
}

int BoundingBox::maxDimension() const
{
    if (type == "Oriented") {
        return -1;
    }
    
    int result = 0;
    if (extent.y() > extent.x()) result = 1;
    if (extent.z() > extent.y() && extent.z() > extent.x()) result = 2;
    
    return result;
}

bool BoundingBox::contains(const BoundingBox& boundingBox, double& dist) const
{
    Eigen::Vector3d bMin = boundingBox.min;
    Eigen::Vector3d bMax = boundingBox.max;
    
    if (((min.x() <= bMin.x() && bMin.x() <= max.x()) || (bMin.x() <= min.x() && min.x() <= bMax.x())) &&
        ((min.y() <= bMin.y() && bMin.y() <= max.y()) || (bMin.y() <= min.y() && min.y() <= bMax.y())) &&
        ((min.z() <= bMin.z() && bMin.z() <= max.z()) || (bMin.z() <= min.z() && min.z() <= bMax.z()))) {
        
        Eigen::Vector3d v = ((min + max)/2) - ((bMin + bMax)/2);
        dist = v.norm();
        return true;
    }
    
    return false;
}

void BoundingBox::computeAxisAlignedBox(std::vector<Vertex>& vertices)
{
    type = "Axis Aligned";
    
    min.setZero();
    max.setZero();
    
    for (VertexIter v = vertices.begin(); v != vertices.end(); v++) {
        expandToInclude(v->position);
    }
}

void BoundingBox::computeOrientedBox(std::vector<Vertex>& vertices) {
        if (vertices.empty()) return;

        // Compute the centroid
        Eigen::Vector3d centroid(0, 0, 0);
        for (const auto& vertex : vertices) {
            centroid += vertex.position;
        }
        centroid /= vertices.size();

        // Compute the covariance matrix
        Eigen::Matrix3d covariance = Eigen::Matrix3d::Zero();
        for (const auto& vertex : vertices) {
            Eigen::Vector3d centered = vertex.position - centroid;
            covariance += centered * centered.transpose();
        }
        covariance /= vertices.size();

        // Add a regularization term to the covariance matrix
        covariance += Eigen::Matrix3d::Identity() * 1e-5;

        // Perform eigen decomposition
        Eigen::SelfAdjointEigenSolver<Eigen::Matrix3d> eigenSolver(covariance);
        if (eigenSolver.info() != Eigen::Success) {
            std::cerr << "Eigen decomposition failed!" << std::endl;
            return;
        }

        Eigen::Vector3d eigenValues = eigenSolver.eigenvalues();
        Eigen::Matrix3d eigenVectors = eigenSolver.eigenvectors();

        // Compute the oriented bounding box corners
        Eigen::Vector3d minProj = Eigen::Vector3d::Constant(INFINITY);
        Eigen::Vector3d maxProj = Eigen::Vector3d::Constant(-INFINITY);
        for (const auto& vertex : vertices) {
            Eigen::Vector3d proj = eigenVectors.transpose() * (vertex.position - centroid);
            minProj = minProj.cwiseMin(proj);
            maxProj = maxProj.cwiseMax(proj);
        }

        Eigen::Vector3d corners[8];
        for (int i = 0; i < 8; ++i) {
            Eigen::Vector3d offset(
                (i & 1) ? maxProj.x() : minProj.x(),
                (i & 2) ? maxProj.y() : minProj.y(),
                (i & 4) ? maxProj.z() : minProj.z()
            );
            corners[i] = centroid + eigenVectors * offset;
        }
        // Compute the oriented bounding box points
        vertices.reserve(8);
        for (int i = 0; i < 8; ++i) {
            orientedPoints.push_back(corners[i]);
        }



    }



extern "C" {
    EXPORT void pinvoke_compute_oriented_box(int pointCount, double* points, double* orientedBoxPoints) {
        BoundingBox bbox;
        std::vector<Vertex> vertices(pointCount);
        for (int i = 0; i < pointCount; ++i) {
            vertices[i].position = Eigen::Vector3d(points[i * 3], points[i * 3 + 1], points[i * 3 + 2]);
        }
        bbox.computeOrientedBox(vertices);

        // Copy the oriented points to the output array
        for (size_t i = 0; i < bbox.orientedPoints.size(); ++i) {
            orientedBoxPoints[i * 3] = bbox.orientedPoints[i].x();
            orientedBoxPoints[i * 3 + 1] = bbox.orientedPoints[i].y();
            orientedBoxPoints[i * 3 + 2] = bbox.orientedPoints[i].z();
        }
    }
}